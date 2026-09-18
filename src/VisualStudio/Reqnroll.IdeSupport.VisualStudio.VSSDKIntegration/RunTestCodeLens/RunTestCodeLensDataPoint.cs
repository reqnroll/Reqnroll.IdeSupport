#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Core.Imaging;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
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
internal sealed class RunTestCodeLensDataPoint : IAsyncCodeLensDataPoint
{
    private readonly ICodeLensCallbackService _callbackService;
    private readonly string _fileUri;
    private readonly int _line;
    private readonly IIdeSupportLogger _logger;

    private IReadOnlyList<TestMethodIdentifier> _cachedMethods = Array.Empty<TestMethodIdentifier>();
    private RunTestOutcomeEntry? _cachedOutcome;

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
    /// <remarks>Never raised in this first pass — same reasoning as <c>HookCodeLensDataPoint</c>: no disposal hook exists to safely unsubscribe from a shared invalidation source, and labels still refresh naturally whenever CodeLens re-creates data points.</remarks>
    public event AsyncEventHandler? InvalidatedAsync { add { } remove { } }

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
            // Clear alongside _cachedMethods: a stale entry here would leave GetDetailsAsync
            // rendering a previous outcome's row table for a line that just resolved to no target at
            // all (fresh-eyes review finding — this can happen on a transient failure of the callback
            // above, not just a genuinely deleted scenario).
            _cachedOutcome = null;
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

        // "Scenarios" (plural) for a Scenario Outline — running it runs every Examples: row, not a
        // single case — "Scenario" for a plain scenario. All entries on one line share the same
        // IsScenarioOutline value (they come from a single symbol node), so the first is enough.
        var label = onThisLine[0].IsScenarioOutline ? "▶ Run Scenarios" : "▶ Run Scenario";

        // Pass/fail glyph. Source of truth is the LSP server's TestOutcomeStore, fed by the bundled
        // VSTest logger (implementation plan §4.2) — it sees every result of an IDE-triggered run, including
        // each Scenario Outline row (issue #702). Only when the store has never heard of this method
        // (no run yet this session, or a project the logger can't reach — e.g. Microsoft.Testing.Platform)
        // do we fall back to RunTestOutcomeBridge's reflection into VS's own TestStore, which degrades
        // to "no glyph" on any failure. Only the first target's outcome is used — good enough for the
        // common single-method case; a mixed-outcome multi-target Outline (allowRowTests = false) just
        // shows the first target's state, not an aggregate.
        ImageId? imageId = null;
        string outcomeSource;
        var primary = _cachedMethods[0];
        _cachedOutcome = await TryGetStoredOutcomeAsync(primary, token).ConfigureAwait(false);
        if (_cachedOutcome is { IsRunning: true })
        {
            // A run naming this method is in flight: VS's own lens shows a spinner here.
            imageId = ToImageId(KnownMonikers.StatusRunning);
            outcomeSource = "store:running";
        }
        else if (_cachedOutcome is { IsStale: true })
        {
            // Recorded before the container was last rebuilt, or just too old to keep trusting (see
            // RunTestCodeLensCallbackListener.IsStale's remarks) — say nothing rather than something
            // outdated, and don't ask the bridge either: VS's TestStore would just repeat the stale
            // value. Also clear the cache itself, not just the glyph: a details-pane click must not
            // render this stale entry's row table as if it were current (fresh-eyes review finding).
            outcomeSource = $"store:stale({_cachedOutcome.Aggregate})";
            _cachedOutcome = null;
        }
        else if (_cachedOutcome is not null && RunTestOutcomeBridge.ParseOutcome(_cachedOutcome.Aggregate) is { } storedOutcome)
        {
            imageId = RunTestOutcomeBridge.ToImageId(storedOutcome);
            outcomeSource = $"store:{_cachedOutcome.Aggregate}";
        }
        else
        {
            var bridged = await RunTestOutcomeBridge.TryGetOutcomeAsync(primary, token).ConfigureAwait(false);
            if (bridged is { } resolvedOutcome)
                imageId = RunTestOutcomeBridge.ToImageId(resolvedOutcome);
            outcomeSource = $"bridge:{bridged?.ToString() ?? "(none)"}";
        }

        _logger.LogVerbose($"RunTestCodeLensDataPoint: GetDataAsync — resolved {_cachedMethods.Count} method(s) for line={_line}, label='{label}', outcome={outcomeSource}");

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

        // Per-row outcome table (implementation plan Phase 2): one entry per test case the logger
        // reported for this method — one per Examples row for an outline — with the failing step
        // Reqnroll's own trace attributes the failure to (not the last step, which is what the stack
        // trace would say). Empty when no run has reported this method this session.
        var (headers, entries) = BuildOutcomeTable(_cachedOutcome);

        _logger.LogVerbose($"RunTestCodeLensDataPoint: GetDetailsAsync — line={_line}, cachedMethods={methods.Count}, commands={commands.Count}, rows={entries.Count}");

        return Task.FromResult(new CodeLensDetailsDescriptor
        {
            Headers = headers,
            Entries = entries,
            PaneNavigationCommands = commands,
        });
    }

    internal static (IReadOnlyList<CodeLensDetailHeaderDescriptor> Headers, IReadOnlyList<CodeLensDetailEntryDescriptor> Entries) BuildOutcomeTable(RunTestOutcomeEntry? outcome)
    {
        if (outcome is null || outcome.Rows.Count == 0)
            return (Array.Empty<CodeLensDetailHeaderDescriptor>(), Array.Empty<CodeLensDetailEntryDescriptor>());

        var headers = new[]
        {
            new CodeLensDetailHeaderDescriptor { UniqueName = "example",    DisplayName = "Example",     Width = 0.35 },
            new CodeLensDetailHeaderDescriptor { UniqueName = "outcome",    DisplayName = "Outcome",     Width = 0.12 },
            new CodeLensDetailHeaderDescriptor { UniqueName = "duration",   DisplayName = "Duration",    Width = 0.10 },
            new CodeLensDetailHeaderDescriptor { UniqueName = "failedStep", DisplayName = "Failed step", Width = 0.43 },
        };

        var entries = outcome.Rows.Select(row =>
        {
            var failedStep = row.FailedStepText is null
                ? string.Empty
                : $"{row.FailedStepText} ({DescribeStepOutcome(row.FailedStepOutcome)}, step {row.FailedStepIndex + 1} of {row.StepCount})";
            return new CodeLensDetailEntryDescriptor
            {
                Fields = new[]
                {
                    new CodeLensDetailEntryField { Text = row.DisplayName },
                    new CodeLensDetailEntryField { Text = row.Outcome },
                    new CodeLensDetailEntryField { Text = FormatDuration(row.DurationMs) },
                    new CodeLensDetailEntryField { Text = failedStep },
                },
                Tooltip = row.ErrorMessage ?? row.DisplayName,
            };
        }).ToList();

        return (headers, entries);
    }

    private static ImageId ToImageId(ImageMoniker moniker) => new(moniker.Guid, moniker.Id);

    private static string DescribeStepOutcome(string? stepOutcome) => stepOutcome switch
    {
        "Error" => "threw",
        "BindingError" => "binding error",
        "Undefined" => "undefined step",
        null => "failed",
        _ => stepOutcome,
    };

    private static string FormatDuration(double milliseconds)
        => milliseconds >= 1000
            ? (milliseconds / 1000).ToString("0.0 s", System.Globalization.CultureInfo.InvariantCulture)
            : milliseconds.ToString("0 ms", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Asks the LSP server's <c>TestOutcomeStore</c> (via the same OOP→in-proc→LSP callback channel as
    /// target resolution) for this method's last-known outcome. Null on "unknown" <em>and</em> on any failure —
    /// the caller then consults the reflection bridge, so a broken callback never costs the glyph.
    /// </summary>
    private async Task<RunTestOutcomeEntry?> TryGetStoredOutcomeAsync(TestMethodIdentifier method, CancellationToken token)
    {
        try
        {
            return await _callbackService
                .InvokeAsync<RunTestOutcomeEntry?>(this, RunTestCodeLensCallbackListener.GetOutcomeMethod,
                    new object[] { method.OutputFilePath, method.ManagedType, method.ManagedMethod }, token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            _logger.LogException(ex, $"RunTestCodeLensDataPoint: {RunTestCodeLensCallbackListener.GetOutcomeMethod} failed for {method.ManagedType}.{method.ManagedMethod}; falling back to the bridge");
            return null;
        }
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
