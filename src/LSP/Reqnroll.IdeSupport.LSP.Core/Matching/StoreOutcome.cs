namespace Reqnroll.IdeSupport.LSP.Core.Matching;

/// <summary>
/// What <see cref="IBindingMatchService.Store"/> concluded about an incoming match set when it
/// compared the set's <see cref="DerivationStamp"/> with the document's current content generation
/// and with the entry already stored under the same key.
/// </summary>
/// <remarks>
/// The guard is observational for now: a rejected write is still stored. The outcome says what an
/// enforcing store would have done.
/// </remarks>
public enum StoreOutcome
{
    /// <summary>The write was built from the document's current text and is not older than the stored entry.</summary>
    Accepted,

    /// <summary>The document's content generation moved on after the inputs of this write were read.</summary>
    RejectedSuperseded,

    /// <summary>The write is older than the entry already stored under the same key.</summary>
    RejectedStale,
}

/// <summary>Running totals of <see cref="StoreOutcome"/> values since the service was created.</summary>
public readonly record struct StoreStats(long AcceptedWrites, long RejectedSuperseded, long RejectedStale);
