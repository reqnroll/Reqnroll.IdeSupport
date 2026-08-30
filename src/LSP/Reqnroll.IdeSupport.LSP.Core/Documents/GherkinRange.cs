namespace Reqnroll.IdeSupport.LSP.Core.Documents;

/// <summary>GherkinRange</summary>
public class GherkinRange : IEquatable<GherkinRange>
{
    /// <summary>Initializes a new instance of the <see cref="GherkinRange"/> class.</summary>
    public GherkinRange(IGherkinTextSnapshot snapshot, int start, int length) 
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        Start = start;
        Length = length;
    }
    /// <summary>Gets the text snapshot this range is defined against.</summary>
    public IGherkinTextSnapshot Snapshot { get; }
    /// <summary>Gets the zero-based character offset where the range starts.</summary>
    public int Start { get; }
    /// <summary>Gets the length of the range, in characters.</summary>
    public int Length { get; }
    /// <summary>Gets the exclusive character offset where the range ends (<c>Start + Length</c>).</summary>
    public int End => Start + Length;

    // Mirrors SnapshotSpan(startLine.Start, endLine.End) construction pattern
    /// <summary>Creates a range spanning from the start of <paramref name="startLine"/> to the end of <paramref name="endLine"/>.</summary>
    public static GherkinRange FromLines(
        IGherkinTextSnapshot snapshot,
        IGherkinTextSnapshotLine startLine,
        IGherkinTextSnapshotLine endLine)
    {
        return new GherkinRange(snapshot, startLine.Start, endLine.End - startLine.Start);
    }

    // Mirrors new SnapshotSpan(startPoint, length) construction pattern
    /// <summary>Creates a range starting at <paramref name="startOffset"/> with the given <paramref name="length"/>, validated against the snapshot's bounds.</summary>
    public static GherkinRange FromPoint(
        IGherkinTextSnapshot snapshot, int startOffset, int length)
    {
        if (snapshot is null)
            throw new ArgumentNullException(nameof(snapshot));
        if (startOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(startOffset), startOffset, "Start offset must be non-negative.");
        if (startOffset > snapshot.Length)
            throw new ArgumentOutOfRangeException(nameof(startOffset), startOffset, "Start offset must not exceed the snapshot length.");
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length), length, "Length must be non-negative.");
        if (startOffset + length > snapshot.Length)
            throw new ArgumentOutOfRangeException(nameof(length), length, "The span (startOffset + length) exceeds the snapshot length.");

        return new GherkinRange(snapshot, startOffset, length);
    }

    // Mirrors SnapshotSpan.IntersectsWith
    // Two ranges intersect if they have positions in common, or the end of one
    // coincides with the start of the other — provided neither range is empty.
    /// <summary>Determines whether this range and <paramref name="other"/> share any positions (touching end-to-start counts only when both ranges are non-empty).</summary>
    public bool IntersectsWith(GherkinRange other)
    {
        if (other is null)
            throw new ArgumentNullException(nameof(other));
        if (!ReferenceEquals(Snapshot, other.Snapshot))
            throw new ArgumentException("Ranges must refer to the same snapshot.", nameof(other));

        // Touching empty spans do NOT count as intersecting (mirrors SnapshotSpan behaviour)
        if (Length == 0 || other.Length == 0)
            return End > other.Start && Start < other.End;

        // Non-empty spans: touching end-to-start counts as intersecting
        return End >= other.Start && Start <= other.End;
    }

    /// <summary>Determines whether this range refers to the same snapshot, start, and length as <paramref name="other"/>.</summary>
    public bool Equals(GherkinRange other)
    {
        if (other is null)
            return false;

        return Start == other.Start
            && Length == other.Length
            && ReferenceEquals(Snapshot, other.Snapshot);
    }

    // Line/character resolution — needed by LSP response mapping
    /// <summary>Gets the (line, character) position of the start of the range.</summary>
    public (int Line, int Character) StartLinePosition => ResolveOffset(Snapshot, Start);
    /// <summary>Gets the (line, character) position of the end of the range.</summary>
    public (int Line, int Character) EndLinePosition   => ResolveOffset(Snapshot, End);

    /// <summary>
    /// Converts an absolute character offset to a (line, character) pair using the given snapshot.
    /// Lines and characters are both 0-based (LSP convention).
    /// </summary>
    /// <remarks>
    /// Binary search over each line's <c>End</c> offset (issue #471): lines are produced in
    /// increasing order by <see cref="IGherkinTextSnapshot.GetLineFromLineNumber"/>'s backing
    /// store, so a linear scan from line 0 here turned every call into an O(document line count)
    /// operation -- called several times per symbol/token/range across the codebase (document
    /// outline, folding, inlay hints, semantic tokens, rename, find-usages, diagnostics), this
    /// made position resolution the dominant cost on large files (confirmed live: 6.6s for one
    /// <c>reqnroll/documentSymbolHierarchical</c> call on an 18k-line feature file). Finds the
    /// smallest line index whose <c>End</c> is &gt;= <paramref name="offset"/>, matching the
    /// original linear scan's semantics exactly, including its out-of-bounds fallback (an offset
    /// past the last line's End clamps to that line's length rather than producing a negative or
    /// overlong character offset).
    /// </remarks>
    internal static (int Line, int Character) ResolveOffset(IGherkinTextSnapshot snapshot, int offset)
    {
        int lo = 0;
        int hi = snapshot.LineCount - 1;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            var line = snapshot.GetLineFromLineNumber(mid);
            if (offset <= line.End)
                hi = mid;
            else
                lo = mid + 1;
        }

        var resolved = snapshot.GetLineFromLineNumber(lo);
        if (offset <= resolved.End)
            return (lo, offset - resolved.Start);

        // offset is past every line's End (out-of-bounds) -- clamp to the last line's length,
        // matching the previous linear scan's fallback.
        return (lo, resolved.End - resolved.Start);
    }

    // Used by VoidIdeSupportTag
    /// <summary>A zero-length placeholder range backed by a null snapshot, used where no real range applies.</summary>
    public static readonly GherkinRange Empty = new GherkinRange(NullSnapshot.Instance, 0, 0);
}