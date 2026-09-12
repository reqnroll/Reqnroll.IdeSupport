namespace Reqnroll.IdeSupport.LSP.Server.Performance;

/// <summary>Default <see cref="IFeatureUsageCounters"/>, backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>.</summary>
/// <remarks>
/// <see cref="ConcurrentDictionary{TKey,TValue}.AddOrUpdate(TKey,TValue,Func{TKey,TValue,TValue})"/>'s
/// update factory runs outside the bucket lock (the #554 lesson recorded in
/// <c>Pipeline/ParseCoordinator.cs</c>), so it must be pure — a plain <c>count + 1</c> is. Draining
/// claims each key individually via <c>TryRemove</c> on a key snapshot, the same pattern
/// <c>Pipeline/RefreshDebouncer.Dispose</c> and <c>Pipeline/FeatureRescanDebouncer.Dispose</c> use
/// to claim pending entries: iterating <see cref="ConcurrentDictionary{TKey,TValue}.Values"/> and
/// then clearing would lose any increment that lands between the read and the clear, whereas an
/// atomic per-key <c>TryRemove</c> means a racing <see cref="Increment"/> either updates the entry
/// this drain goes on to remove (so it's included) or creates a fresh entry after the removal (so
/// it survives intact for the next drain) — never both lost.
/// </remarks>
public sealed class FeatureUsageCounters : IFeatureUsageCounters
{
    private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public void Increment(string operation) =>
        _counts.AddOrUpdate(operation, 1, static (_, count) => count + 1);

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, long> Drain()
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var key in _counts.Keys.ToList())
        {
            if (_counts.TryRemove(key, out var value))
                result[key] = value;
        }
        return result;
    }
}
