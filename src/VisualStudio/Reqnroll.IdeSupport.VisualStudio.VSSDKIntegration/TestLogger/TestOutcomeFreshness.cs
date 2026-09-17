using System;
using System.IO;

namespace Reqnroll.IdeSupport.VisualStudio.TestLogger;

/// <summary>
/// The one rule for "is a recorded outcome still trustworthy for its container": used both when
/// deciding whether to keep a persisted entry (<see cref="TestOutcomePersistence"/>) and when
/// deciding whether to render a live one (<see cref="RunTestCodeLens.RunTestCodeLensCallbackListener"/>).
/// </summary>
/// <remarks>
/// Previously implemented twice, independently, with opposite behaviour on the one path that isn't
/// "container missing": if the container exists but the stat itself throws (a sharing violation from
/// an AV scanner, a permissions change, or a delete racing the check), one copy treated that as fresh
/// and the other as stale. Fixed by having both go through the same conservative rule — anything we
/// can't positively confirm is fresh is treated as not fresh.
/// </remarks>
internal static class TestOutcomeFreshness
{
    /// <summary>True when the source container still exists and hasn't been rebuilt since <paramref name="lastUpdatedUtc"/>.</summary>
    public static bool IsFresh(string source, DateTime lastUpdatedUtc, Func<string, DateTime?> sourceLastWriteUtc)
    {
        var written = sourceLastWriteUtc(source);
        return written is not null && written.Value <= lastUpdatedUtc;
    }

    /// <summary>Default <c>sourceLastWriteUtc</c>: null on a missing file or any stat failure — both mean "can't confirm fresh".</summary>
    public static DateTime? DefaultSourceLastWriteUtc(string source)
    {
        try
        {
            return File.Exists(source) ? File.GetLastWriteTimeUtc(source) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
