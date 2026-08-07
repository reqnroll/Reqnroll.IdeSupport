#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Language.CodeLens.Remoting;
using Microsoft.VisualStudio.TestWindow;
using Microsoft.VisualStudio.Threading;

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

    private IReadOnlyList<TestMethodIdentifier> _cachedMethods = Array.Empty<TestMethodIdentifier>();

    public RunTestCodeLensDataPoint(CodeLensDescriptor descriptor, ICodeLensCallbackService callbackService, string fileUri, int line)
    {
        Descriptor = descriptor;
        _callbackService = callbackService;
        _fileUri = fileUri;
        _line = line;
    }

    /// <inheritdoc />
    public CodeLensDescriptor Descriptor { get; }

    /// <inheritdoc />
    /// <remarks>Never raised in this first pass — same reasoning as <c>HookCodeLensDataPoint</c>: no disposal hook exists to safely unsubscribe from a shared invalidation source, and labels still refresh naturally whenever CodeLens re-creates data points.</remarks>
    public event AsyncEventHandler? InvalidatedAsync { add { } remove { } }

    /// <inheritdoc />
    public async Task<CodeLensDataPointDescriptor> GetDataAsync(CodeLensDescriptorContext descriptorContext, CancellationToken token)
    {
        IReadOnlyList<RunTestTargetEntry> entries;
        try
        {
            entries = await _callbackService
                .InvokeAsync<IReadOnlyList<RunTestTargetEntry>>(this, RunTestCodeLensCallbackListener.GetTargetsMethod, new object[] { _fileUri }, token)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            entries = Array.Empty<RunTestTargetEntry>();
        }

        var onThisLine = entries.Where(e => e.Line == _line).ToList();
        if (onThisLine.Count == 0)
        {
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

        // Pre-fetch/cache now (see this type's remarks) — nothing further to resolve for GetDetailsAsync.
        return new CodeLensDataPointDescriptor { Description = "Run" };
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
        }

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
