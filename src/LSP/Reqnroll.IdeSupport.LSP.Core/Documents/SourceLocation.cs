#nullable disable
using System;
using Reqnroll.IdeSupport.Common.ProjectSystem;

namespace Reqnroll.IdeSupport.LSP.Core.Documents;

/// <summary>SourceLocation</summary>
public class SourceLocation
{
    /// <summary>Initializes a new instance of the <see cref="SourceLocation"/> class for a source
    /// file that exists on this machine.</summary>
    public SourceLocation(string sourceFile, int sourceFileLine, int sourceFileColumn, int? sourceFileEndLine = null,
        int? sourceFileEndColumn = null)
        : this(sourceFile, sourceFile, true, sourceFileLine, sourceFileColumn, sourceFileEndLine, sourceFileEndColumn)
    {
    }

    private SourceLocation(string sourceFile, string recordedSourceFile, bool isResolved,
        int sourceFileLine, int sourceFileColumn, int? sourceFileEndLine, int? sourceFileEndColumn)
    {
        SourceFile = sourceFile;
        RecordedSourceFile = recordedSourceFile;
        IsResolved = isResolved;
        SourceFileLine = sourceFileLine;
        SourceFileColumn = sourceFileColumn;
        SourceFileEndLine = sourceFileEndLine;
        SourceFileEndColumn = sourceFileEndColumn;
    }

    /// <summary>
    /// Creates a location whose source file could not be resolved to anything on this machine —
    /// see <see cref="IsResolved"/>.
    /// </summary>
    public static SourceLocation Unresolved(string recordedSourceFile, int sourceFileLine, int sourceFileColumn,
        int? sourceFileEndLine = null, int? sourceFileEndColumn = null) =>
        new(recordedSourceFile, recordedSourceFile, false, sourceFileLine, sourceFileColumn,
            sourceFileEndLine, sourceFileEndColumn);

    /// <summary>
    /// Creates a resolved location whose local path differs from the one discovery recorded —
    /// a foreign path that <see cref="Bindings.ISourceFileResolver"/> mapped onto this machine.
    /// </summary>
    /// <remarks>
    /// The public constructor sets <see cref="RecordedSourceFile"/> to the path it is given, which
    /// is correct only when no remapping happened. Using it for a remapped location would throw the
    /// recorded path away — and the recorded path is the whole reason to keep the distinction: it
    /// is what tells a reader that these bindings came from a container or CI build.
    /// </remarks>
    public static SourceLocation Resolved(string resolvedSourceFile, string recordedSourceFile,
        int sourceFileLine, int sourceFileColumn, int? sourceFileEndLine = null, int? sourceFileEndColumn = null) =>
        new(resolvedSourceFile, recordedSourceFile, true, sourceFileLine, sourceFileColumn,
            sourceFileEndLine, sourceFileEndColumn);

    /// <summary>Returns a copy of this location with its position replaced, keeping the file and its resolution state.</summary>
    public SourceLocation WithPosition(int sourceFileLine, int sourceFileColumn,
        int? sourceFileEndLine = null, int? sourceFileEndColumn = null) =>
        new(SourceFile, RecordedSourceFile, IsResolved, sourceFileLine, sourceFileColumn,
            sourceFileEndLine, sourceFileEndColumn);

    /// <summary>Gets the path of the source file.</summary>
    public string SourceFile { get; }

    /// <summary>
    /// Gets the source-file path exactly as binding discovery recorded it, before any resolution.
    /// Equal to <see cref="SourceFile"/> unless a foreign path was remapped onto this machine.
    /// </summary>
    /// <remarks>
    /// Kept because it is the only thing that explains an <see cref="IsResolved"/> of
    /// <see langword="false"/> to a human: "the assembly records this method at
    /// <c>/workspaces/host-solution/Support/Hooks.cs</c>" is actionable, "navigation did nothing"
    /// is not.
    /// </remarks>
    public string RecordedSourceFile { get; }

    /// <summary>
    /// Whether <see cref="SourceFile"/> names a file that exists on this machine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see langword="false"/> means discovery recorded a path that could not be mapped onto
    /// anything local — see <see cref="Bindings.ProjectSourceFileResolver"/> for the mapping rules
    /// and issue #540 for how this arises (a devcontainer or CI build, an external binding assembly
    /// from NuGet, a solution built on another machine).
    /// </para>
    /// <para>
    /// <b>Callers that turn a location into a navigation target must check this and omit the target
    /// when it is <see langword="false"/>.</b> Emitting one anyway is what the issue's incident
    /// actually was: <c>DocumentUri.FromFileSystemPath("/workspaces/host-solution/Hooks.cs")</c>
    /// yields a perfectly well-formed <c>file:///workspaces/host-solution/Hooks.cs</c>, the IDE is
    /// told to navigate to a file that does not exist, and Go To Definition, hook navigation and
    /// inlay hints all do nothing with no diagnostic anywhere. Skipping the target and logging the
    /// recorded path is the agreed behaviour; silence is not.
    /// </para>
    /// <para>
    /// <see langword="true"/> for every location built by the public constructor, which is what
    /// Roslyn/source-level discovery uses — those paths come from live LSP document URIs and are
    /// local by construction.
    /// </para>
    /// </remarks>
    public bool IsResolved { get; }
    /// <summary>Gets the 1-based line number where the location begins.</summary>
    public int SourceFileLine { get; } // 1-based
    /// <summary>Gets the 1-based column number where the location begins.</summary>
    public int SourceFileColumn { get; } // 1-based
    /// <summary>Gets the 1-based line number where the location ends, if known.</summary>
    public int? SourceFileEndLine { get; } // 1-based
    /// <summary>Gets the 1-based column number where the location ends, if known.</summary>
    public int? SourceFileEndColumn { get; } // 1-based

    /// <summary>Gets whether both an end line and end column are set.</summary>
    public bool HasEndPosition => SourceFileEndLine != null && SourceFileEndColumn != null;

    /// <summary>Returns <see langword="true"/> when <paramref name="line1Based"/> falls within
    /// the span [<see cref="SourceFileLine"/>, <see cref="SourceFileEndLine"/>].</summary>
    public bool ContainsLine(int line1Based)
    {
        var endLine = SourceFileEndLine ?? SourceFileLine;
        return line1Based >= SourceFileLine && line1Based <= endLine;
    }

    /// <summary>Formats the location as <c>file(line,column)</c>.</summary>
    public override string ToString() => $"{SourceFile}({SourceFileLine},{SourceFileColumn})";

    /// <summary>
    /// Value equality: two locations are equal when they name the same file (compared with
    /// <see cref="PathUtils.IsSamePath"/>) at the same start and end position.
    /// </summary>
    /// <remarks>
    /// This type is a value object, but it used to inherit reference equality from <see cref="object"/>.
    /// That silently broke <c>ProjectBindingImplementationEqualityComparer</c>, whose own summary
    /// promises comparison "by method, parameter types, and source location rather than reference
    /// identity" — its <c>Equals(x.SourceLocation, y.SourceLocation)</c> leg was a reference compare,
    /// so two structurally identical implementations produced by two discovery passes never compared
    /// equal. Masked in practice only because <c>BindingImporter</c> shares one implementation
    /// instance per method within a pass; it stopped being masked as soon as connector-imported and
    /// Roslyn-parsed bindings for the same method coexisted (issue #540 F6).
    /// </remarks>
    public override bool Equals(object obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj is not SourceLocation other) return false;

        return SourceFileLine == other.SourceFileLine
               && SourceFileColumn == other.SourceFileColumn
               && SourceFileEndLine == other.SourceFileEndLine
               && SourceFileEndColumn == other.SourceFileEndColumn
               && SameFile(SourceFile, other.SourceFile);
    }

    // PathUtils.IsSamePath deliberately answers false for an absent path -- "no file" is not the
    // same file as anything, including another "no file", which is the right answer when deciding
    // whether a binding belongs to a document. Equals needs the other convention: two locations
    // that both carry no path must still compare equal to each other, or this override would not
    // even be reflexive.
    private static bool SameFile(string a, string b)
    {
        var aEmpty = string.IsNullOrWhiteSpace(a);
        var bEmpty = string.IsNullOrWhiteSpace(b);
        if (aEmpty || bEmpty)
            return aEmpty && bEmpty;

        return PathUtils.IsSamePath(a, b);
    }

    /// <summary>Computes a hash code consistent with <see cref="Equals(object)"/>.</summary>
    public override int GetHashCode()
    {
        // The file leg hashes the normalized path, case-folded, so it agrees with IsSamePath's
        // comparison; a null/whitespace path hashes to 0 (and never compares equal anyway).
        var fileHash = string.IsNullOrWhiteSpace(SourceFile)
            ? 0
            : StringComparer.OrdinalIgnoreCase.GetHashCode(PathUtils.NormalizeForComparison(SourceFile));

        unchecked
        {
            var hash = 17;
            hash = hash * 23 + fileHash;
            hash = hash * 23 + SourceFileLine;
            hash = hash * 23 + SourceFileColumn;
            hash = hash * 23 + (SourceFileEndLine ?? -1);
            hash = hash * 23 + (SourceFileEndColumn ?? -1);
            return hash;
        }
    }
}
