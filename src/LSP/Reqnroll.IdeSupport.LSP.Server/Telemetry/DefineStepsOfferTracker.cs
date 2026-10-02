#nullable enable
using System.Text.RegularExpressions;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Matching;

namespace Reqnroll.IdeSupport.LSP.Server.Telemetry;

/// <summary>
/// Remembers, in memory only, which undefined steps of a feature file the "Define step(s)" quick fix
/// was recently offered for, and reports a <see cref="TelemetryEvents.StepDefined"/> event when a later
/// re-match shows those steps resolving to a binding (issue #847).
/// </summary>
/// <remarks>
/// The quick fix's <c>WorkspaceEdit</c> is applied entirely client-side, so the server never sees an
/// acceptance. What it does see is the consequence: the edit lands in a <c>.cs</c> buffer, Roslyn
/// (or the connector after a build) rediscovers bindings, and the feature is re-matched with the step
/// now defined. Correlating that with the earlier offer works identically in every IDE and also counts
/// hand-written definitions (<see cref="TelemetryProperties.StepDefinedVia.Other"/>).
/// </remarks>
public interface IDefineStepsOfferTracker
{
    /// <summary>
    /// Records that the quick fix was offered for <paramref name="undefinedSteps"/> of
    /// <paramref name="featureDocumentId"/>, replacing any earlier offer for the same feature.
    /// </summary>
    /// <param name="featureDocumentId">URI string of the feature file.</param>
    /// <param name="undefinedSteps">The steps the offered actions would define.</param>
    /// <param name="style">The snippet expression style the offered skeletons use.</param>
    /// <param name="newFilePaths">Paths of the files the offered "create file" actions would create.</param>
    /// <param name="appendFilePaths">Paths of the existing files the offered "append" actions would edit.</param>
    void RecordOffer(
        string featureDocumentId,
        IEnumerable<StepBindingMatch> undefinedSteps,
        SnippetExpressionStyle style,
        IEnumerable<string> newFilePaths,
        IEnumerable<string> appendFilePaths);

    /// <summary>
    /// Inspects a freshly computed match set and emits <see cref="TelemetryEvents.StepDefined"/> for
    /// every step offered earlier (and not yet expired) that is now defined. A no-op for a feature
    /// with no pending offer.
    /// </summary>
    void Observe(FeatureBindingMatchSet matchSet);
}

/// <inheritdoc cref="IDefineStepsOfferTracker"/>
public sealed class DefineStepsOfferTracker : IDefineStepsOfferTracker
{
    /// <summary>How long after the latest offer an Undefined-to-Defined transition still counts as a result of it.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(10);

    /// <summary>Most features with a pending offer; the least recently offered is evicted beyond this.</summary>
    public const int MaxTrackedFeatures = 64;

    /// <summary>Most steps remembered per feature offer; further steps are not tracked.</summary>
    public const int MaxStepsPerOffer = 100;

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private sealed class PendingOffer
    {
        public required DateTimeOffset OfferedAt { get; init; }
        public required HashSet<string> Steps { get; init; }
        public required string ExpressionStyle { get; init; }
        public required HashSet<string> NewFiles { get; init; }
        public required HashSet<string> AppendFiles { get; init; }
    }

    private readonly Dictionary<string, PendingOffer> _offers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly ILspTelemetryService? _telemetry;
    private readonly TimeProvider _time;
    private readonly TimeSpan _window;

    /// <summary>Initializes a new instance of the <see cref="DefineStepsOfferTracker"/> class.</summary>
    public DefineStepsOfferTracker(
        ILspTelemetryService? telemetry = null,
        TimeProvider? timeProvider = null,
        TimeSpan? window = null)
    {
        _telemetry = telemetry;
        _time = timeProvider ?? TimeProvider.System;
        _window = window ?? DefaultWindow;
    }

    /// <summary>Number of features with a pending offer (exposed for tests).</summary>
    internal int TrackedFeatureCount
    {
        get { lock (_gate) return _offers.Count; }
    }

    /// <inheritdoc/>
    public void RecordOffer(
        string featureDocumentId,
        IEnumerable<StepBindingMatch> undefinedSteps,
        SnippetExpressionStyle style,
        IEnumerable<string> newFilePaths,
        IEnumerable<string> appendFilePaths)
    {
        if (string.IsNullOrEmpty(featureDocumentId)) return;

        var steps = new HashSet<string>(StringComparer.Ordinal);
        var keyer = new StepKeyer();
        foreach (var step in undefinedSteps)
        {
            var key = keyer.KeyOf(step);
            if (key.Length > 0 && steps.Count < MaxStepsPerOffer)
                steps.Add(key);
        }
        if (steps.Count == 0) return;

        var offer = new PendingOffer
        {
            OfferedAt = _time.GetUtcNow(),
            Steps = steps,
            ExpressionStyle = TelemetryProperties.ExpressionStyleFor(style),
            NewFiles = new HashSet<string>(newFilePaths.Select(NormalizePath), StringComparer.OrdinalIgnoreCase),
            AppendFiles = new HashSet<string>(appendFilePaths.Select(NormalizePath), StringComparer.OrdinalIgnoreCase),
        };

        lock (_gate)
        {
            PurgeExpired(offer.OfferedAt);
            _offers[featureDocumentId] = offer;
            while (_offers.Count > MaxTrackedFeatures)
            {
                var oldest = _offers.MinBy(kv => kv.Value.OfferedAt).Key;
                _offers.Remove(oldest);
            }
        }
    }

    /// <inheritdoc/>
    public void Observe(FeatureBindingMatchSet matchSet)
    {
        List<(string Via, string Style)>? defined = null;

        lock (_gate)
        {
            if (!_offers.TryGetValue(matchSet.DocumentId, out var offer))
                return;

            if (_time.GetUtcNow() - offer.OfferedAt > _window)
            {
                _offers.Remove(matchSet.DocumentId);
                return;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var keyer = new StepKeyer();
            foreach (var step in matchSet.Steps)
            {
                if (!step.IsDefined) continue;
                var key = keyer.KeyOf(step);
                if (!offer.Steps.Contains(key) || !seen.Add(key)) continue;

                (defined ??= new()).Add((ClassifyVia(offer, step), offer.ExpressionStyle));
            }

            if (defined is null) return;

            // Each step is reported once: drop what was just observed, and the whole offer once
            // nothing is left to observe.
            offer.Steps.ExceptWith(seen);
            if (offer.Steps.Count == 0)
                _offers.Remove(matchSet.DocumentId);
        }

        if (_telemetry is null) return;

        foreach (var group in defined.GroupBy(d => (d.Via, d.Style)))
        {
            _telemetry.SendEvent(TelemetryEvents.StepDefined, new()
            {
                [TelemetryProperties.Count] = group.Count(),
                [TelemetryProperties.Via] = group.Key.Via,
                [TelemetryProperties.ExpressionStyle] = group.Key.Style,
            });
        }
    }

    private static string ClassifyVia(PendingOffer offer, StepBindingMatch step)
    {
        foreach (var location in step.BindingLocations)
        {
            var path = NormalizePath(location.SourceFile);
            if (offer.NewFiles.Contains(path)) return TelemetryProperties.StepDefinedVia.QuickFixNewFile;
            if (offer.AppendFiles.Contains(path)) return TelemetryProperties.StepDefinedVia.QuickFixAppend;
        }
        return TelemetryProperties.StepDefinedVia.Other;
    }

    private void PurgeExpired(DateTimeOffset now)
    {
        var expired = _offers.Where(kv => now - kv.Value.OfferedAt > _window).Select(kv => kv.Key).ToList();
        foreach (var key in expired)
            _offers.Remove(key);
    }

    private static string NormalizePath(string path) => (path ?? string.Empty).Replace('\\', '/').Trim();

    /// <summary>
    /// Computes a step's identity within one feature: its step text with whitespace collapsed. Kept in
    /// memory only to correlate offer and re-match — never transmitted. Caches the snapshot text so a
    /// match set's steps (which share one snapshot) don't each copy the whole document.
    /// </summary>
    private sealed class StepKeyer
    {
        private IGherkinTextSnapshot? _snapshot;
        private string _text = string.Empty;

        public string KeyOf(StepBindingMatch step)
        {
            var range = step.Range;
            if (!ReferenceEquals(range.Snapshot, _snapshot))
            {
                _snapshot = range.Snapshot;
                _text = _snapshot.GetText();
            }
            if (range.Start < 0 || range.End > _text.Length) return string.Empty;
            return Whitespace.Replace(_text.Substring(range.Start, range.Length), " ").Trim();
        }
    }
}
