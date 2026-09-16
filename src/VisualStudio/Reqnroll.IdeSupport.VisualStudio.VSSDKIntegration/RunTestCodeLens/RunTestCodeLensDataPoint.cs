#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Core.Imaging;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Language.CodeLens.Remoting;
using Microsoft.VisualStudio.TestWindow;
using Microsoft.VisualStudio.Threading;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;

/// <summary>
/// A single Run CodeLens data point (design doc §5/§6, issue #262): resolves the "Run" label and,
/// on request, wires the Details popup's Run/Debug actions to VS's own internal Test Explorer
/// commands via <see cref="TestExplorerCommandIds"/> — no VS-specific run/debug invocation logic is
/// needed, Test Explorer does the actual work once handed the right <see cref="TestMethodIdentifier"/>.
/// </summary>
/// <remarks>
/// Runs out-of-process; reaches the LSP bridge via <see cref="ICodeLensCallbackService"/> calling
/// back into <see cref="RunTestCodeLensCallbackListener"/> (see <see cref="RunTestCodeLensDataPointProvider"/>'s
/// remarks). <b>Deadlock avoidance</b> follows the exact pattern <c>HookCodeLensDataPoint</c>
/// documents at length (found live via a captured process dump debugging issue #372): VS blocks the
/// UI thread synchronously on <see cref="GetDetailsAsync"/> without pumping the message queue, so
/// that method must never make its own callback round-trip. <see cref="GetDataAsync"/> — which
/// always runs first, on a normal async path — pre-fetches and caches the resolved
/// <see cref="TestMethodIdentifier"/> set here; <see cref="GetDetailsAsync"/> only reads the cache.
/// </remarks>
internal sealed class RunTestCodeLensDataPoint : IAsyncCodeLensDataPoint, IDisposable
{
    private readonly ICodeLensCallbackService _callbackService;
    private readonly string _fileUri;
    private readonly int _line;
    private readonly IIdeSupportLogger _logger;

    // Own subscription identity for RunTestOutcomeBridge.TryGetOutcomeAsync (issue #700 correction) —
    // the server's SubscriptionTracker replaces a dataPointId's whole tracked-method set on every poll
    // rather than merging, so a shared id across every line's data point meant each line's poll evicted
    // every other line's push subscription. One Guid per instance, mirroring VS's own
    // AbstractTestDataPoint.id, keeps each line's subscription independent.
    private readonly Guid _outcomeSubscriptionId = Guid.NewGuid();

    private IReadOnlyList<TestMethodIdentifier> _cachedMethods = Array.Empty<TestMethodIdentifier>();
    // int, not bool: GetDataAsync can run concurrently on the same instance (VS re-queries on
    // incremental refreshes, per its own remarks below), and a plain check-then-act on a bool let two
    // overlapping calls both observe "not yet subscribed" and both subscribe, double-firing
    // InvalidatedAsync per notification. Interlocked.CompareExchange makes "subscribe exactly once"
    // atomic; 0 = not subscribed, 1 = subscribed.
    private int _subscribedToOutcomeChanges;
    private AsyncEventHandler? _invalidatedAsync;

    public RunTestCodeLensDataPoint(CodeLensDescriptor descriptor, ICodeLensCallbackService callbackService, string fileUri, int line, IIdeSupportLogger logger)
    {
        Descriptor = descriptor;
        _callbackService = callbackService;
        _fileUri = fileUri;
        _line = line;
        _logger = logger;
    }

    /// <inheritdoc />
    public CodeLensDescriptor Descriptor { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Real event as of issue #700 — raised from <see cref="OnTestOutcomeChanged"/> when
    /// <see cref="RunTestOutcomeBridge.TestChanged"/> reports a completed run for one of
    /// <see cref="_cachedMethods"/>, so the pass/fail glyph refreshes instead of staying frozen at
    /// whatever it resolved to (usually nothing) the one time <see cref="GetDataAsync"/> happened to
    /// run before any test had executed. Mirrors VS's own <c>AbstractTestDataPoint</c> pattern
    /// (decompiled from <c>Microsoft.VisualStudio.TestWindow.CodeLens.dll</c>): subscribe once test
    /// methods are known, unsubscribe on <see cref="Dispose"/>.
    /// </remarks>
    public event AsyncEventHandler? InvalidatedAsync
    {
        add => _invalidatedAsync += value;
        remove => _invalidatedAsync -= value;
    }

    /// <summary>
    /// Called on <see cref="RunTestOutcomeBridge.TestChanged"/> for every test whose cached outcome
    /// changed process-wide, not just ones this data point cares about — filters to
    /// <see cref="_cachedMethods"/> before invalidating, same as VS's own <c>OnTestChanged</c> override.
    /// The event payload is untyped <see cref="object"/> (see <see cref="RunTestOutcomeBridge.TestChanged"/>'s
    /// remarks for why) — pattern-match to the real type here, where referencing it is already required.
    /// </summary>
    /// <remarks>
    /// Wrapped in try/catch deliberately: this runs inside <see cref="RunTestOutcomeBridge.TestChanged"/>'s
    /// multicast invocation, shared by every other line's data point. An unhandled exception here would
    /// abort that invocation partway through, silently starving every subscriber registered after this
    /// one in the list for that notification — the opposite of this feature's own "never let a failure
    /// here take down the lens" design elsewhere in <see cref="RunTestOutcomeBridge"/>.
    /// </remarks>
    private void OnTestOutcomeChanged(object? sender, object testMethod)
    {
        try
        {
            if (testMethod is not TestMethodIdentifier identifier || !_cachedMethods.Contains(identifier))
                return;

            _logger.LogVerbose($"RunTestCodeLensDataPoint: OnTestOutcomeChanged — line={_line} matched changed test, invalidating.");
            var handler = _invalidatedAsync;
            if (handler is not null)
                TplExtensions.InvokeAsync(handler, this, EventArgs.Empty).Forget();
        }
        catch (Exception ex)
        {
            _logger.LogException(ex, $"RunTestCodeLensDataPoint: OnTestOutcomeChanged — line={_line} failed handling a test-changed notification.");
        }
    }

    /// <inheritdoc />
    /// <remarks>Unsubscribes from <see cref="RunTestOutcomeBridge.TestChanged"/> (issue #700) — without this, every data point CodeLens ever created would stay reachable forever through that static event's invocation list.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _subscribedToOutcomeChanges, 0) != 0)
            RunTestOutcomeBridge.TestChanged -= OnTestOutcomeChanged;
    }

    /// <inheritdoc />
    public async Task<CodeLensDataPointDescriptor> GetDataAsync(CodeLensDescriptorContext descriptorContext, CancellationToken token)
    {
        IReadOnlyList<RunTestTargetEntry> onThisLine;
        try
        {
            _logger.LogVerbose($"RunTestCodeLensDataPoint: GetDataAsync — invoking {RunTestCodeLensCallbackListener.GetTargetsForLineMethod} for {_fileUri} line={_line}");
            onThisLine = await _callbackService
                .InvokeAsync<IReadOnlyList<RunTestTargetEntry>>(this, RunTestCodeLensCallbackListener.GetTargetsForLineMethod, new object[] { _fileUri, _line }, token)
                .ConfigureAwait(false);
            _logger.LogVerbose($"RunTestCodeLensDataPoint: GetDataAsync — callback returned {onThisLine.Count} entr{(onThisLine.Count == 1 ? "y" : "ies")} for {_fileUri} line={_line}");
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            // Previously swallowed with no logging at all — this catch is the only place a failure
            // in the OOP-to-devenv.exe callback round trip (ServiceHub activation, RPC/serialization
            // fault, etc.) could ever surface, and losing it here meant the lens rendered forever in
            // its unresolved/loading state with zero trace anywhere of why (live report: no Details
            // popup ever appeared on click).
            _logger.LogException(ex, $"RunTestCodeLensDataPoint: GetDataAsync — callback to {RunTestCodeLensCallbackListener.GetTargetsForLineMethod} failed for {_fileUri} line={_line}");
            onThisLine = Array.Empty<RunTestTargetEntry>();
        }

        if (onThisLine.Count == 0)
        {
            _logger.LogVerbose($"RunTestCodeLensDataPoint: GetDataAsync — no target resolved for line={_line} in {_fileUri}.");
            _cachedMethods = Array.Empty<TestMethodIdentifier>();
            return new CodeLensDataPointDescriptor { Description = string.Empty };
        }

        // Row-tests targets share one method — collapsing to distinct (assembly, type, method)
        // tuples means "run this scenario" and "run all examples" (row-tests mode) are the same
        // single-element list, matching design doc §5's "free" case. For individual-methods mode
        // (allowRowTests = false) this naturally becomes a multi-element array; whether
        // CodeLensDetailPaneCommand.CommandArgs actually accepts more than one TestMethodIdentifier
        // in that mode is unconfirmed live (design doc §7 item 7) — structurally supported (CommandArgs
        // is IEnumerable<object>), not verified against a real Test Explorer.
        _cachedMethods = onThisLine
            .Select(e => new TestMethodIdentifier(e.OutputAssemblyPath, $"{e.DeclaringTypeFullName}.{e.MethodName}", e.DeclaringTypeFullName, e.MethodName))
            .Distinct()
            .ToList();

        // Subscribe once we actually know which methods to care about (issue #700) — a completed run
        // for any of them should invalidate this lens so the glyph re-fetches. GetDataAsync can run
        // concurrently on the same instance (VS re-queries on incremental refreshes), so the
        // subscribe-exactly-once check has to be atomic, not a plain check-then-act on a bool.
        if (Interlocked.CompareExchange(ref _subscribedToOutcomeChanges, 1, 0) == 0)
            RunTestOutcomeBridge.TestChanged += OnTestOutcomeChanged;

        // "Scenarios" (plural) for a Scenario Outline — running it runs every Examples: row, not a
        // single case — "Scenario" for a plain scenario. All entries on one line share the same
        // IsScenarioOutline value (they come from a single symbol node), so the first is enough.
        var label = onThisLine[0].IsScenarioOutline ? "▶ Run Scenarios" : "▶ Run Scenario";

        // Best-effort pass/fail glyph (issue #504 follow-up) — reflects into an unsupported internal
        // VS API (see RunTestOutcomeBridge's remarks) that degrades to "no glyph" on any failure, so
        // this never blocks the lens itself from rendering. Only the first target's outcome is used —
        // good enough for the common single-method case; a mixed-outcome multi-target Outline
        // (allowRowTests = false) just shows the first target's state, not an aggregate.
        ImageId? imageId = null;
        var outcome = await RunTestOutcomeBridge.TryGetOutcomeAsync(_outcomeSubscriptionId, _cachedMethods[0], token).ConfigureAwait(false);
        if (outcome is { } resolvedOutcome)
            imageId = RunTestOutcomeBridge.ToImageId(resolvedOutcome);

        _logger.LogVerbose($"RunTestCodeLensDataPoint: GetDataAsync — resolved {_cachedMethods.Count} method(s) for line={_line}, label='{label}', outcome={outcome?.ToString() ?? "(none)"}");

        // Pre-fetch/cache now (see this type's remarks) — nothing further to resolve for GetDetailsAsync.
        return new CodeLensDataPointDescriptor { Description = label, ImageId = imageId };
    }

    /// <inheritdoc />
    public Task<CodeLensDetailsDescriptor> GetDetailsAsync(CodeLensDescriptorContext descriptorContext, CancellationToken token)
    {
        // Always populated by GetDataAsync (see this type's remarks) — on the normal click path
        // this makes no further cross-process call, which is what keeps the UI thread from
        // deadlocking. An empty array is the correct answer if this line had no resolved target.
        var methods = _cachedMethods;

        var commands = new List<CodeLensDetailPaneCommand>();
        if (methods.Count > 0)
        {
            commands.Add(BuildCommand("Run", TestExplorerCommandIds.RunCommandId, methods));
            commands.Add(BuildCommand("Debug", TestExplorerCommandIds.DebugCommandId, methods));
            // Reveals the test in the Test Explorer tool window (issue #504 follow-up) — the
            // supported way to reach the native pass/fail glyph and run history VS's own
            // TestStatusProvider CodeLens already shows on the generated .feature.cs method,
            // without this extension needing the internal ICodeLensTestInformationService itself.
            commands.Add(BuildCommand("Show in Test Explorer", TestExplorerCommandIds.SyncCommandId, methods));
        }

        _logger.LogVerbose($"RunTestCodeLensDataPoint: GetDetailsAsync — line={_line}, cachedMethods={methods.Count}, commands={commands.Count}");

        return Task.FromResult(new CodeLensDetailsDescriptor
        {
            Headers = Array.Empty<CodeLensDetailHeaderDescriptor>(),
            Entries = Array.Empty<CodeLensDetailEntryDescriptor>(),
            PaneNavigationCommands = commands,
        });
    }

    private static CodeLensDetailPaneCommand BuildCommand(string displayName, int commandId, IReadOnlyList<TestMethodIdentifier> methods) =>
        new()
        {
            CommandDisplayName = displayName,
            CommandId = new CodeLensDetailEntryCommand
            {
                CommandSet = TestExplorerCommandIds.CommandSet,
                CommandId = commandId,
            },
            CommandArgs = new object[] { methods.ToArray() },
        };
}
