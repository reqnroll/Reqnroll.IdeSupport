#nullable enable

using System;
using System.IO;

namespace Reqnroll.IdeSupport.LSP.Core.TestOutcomes;

/// <summary>
/// Single source of truth for "is this outcome still describing the code that produced it": the
/// container assembly must exist and not have been written to (rebuilt) since the outcome's
/// <c>LastUpdatedUtc</c>. Used by both <see cref="TestOutcomePersistence"/> (load-time pruning) and
/// <c>GetTestOutcomeHandler</c> (per-lookup staleness, in Reqnroll.IdeSupport.LSP.Server), which used
/// to be two independently written copies of this rule that disagreed on the "can't determine the
/// container's write time" path. <c>public</c> (not <c>internal</c>) because that consumer lives in a
/// different assembly.
/// </summary>
public static class TestOutcomeFreshness
{
    public static bool IsFresh(string source, DateTime lastUpdatedUtc, Func<string, DateTime?> sourceLastWriteUtc)
    {
        var written = sourceLastWriteUtc(source);
        return written is not null && written.Value <= lastUpdatedUtc;
    }

    public static DateTime? DefaultSourceLastWriteUtc(string source)
    {
        try { return File.Exists(source) ? File.GetLastWriteTimeUtc(source) : null; }
        catch (Exception) { return null; }
    }
}
