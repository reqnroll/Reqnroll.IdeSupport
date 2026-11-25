#nullable enable

using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Language.CodeLens.Remoting;
using Microsoft.VisualStudio.Utilities;
using Reqnroll.IdeSupport.VisualStudio.LineCodeLens;
using Reqnroll.IdeSupport.VisualStudio.WellKnownIds;

namespace Reqnroll.IdeSupport.VisualStudio.HookCodeLens;

/// <summary>
/// Classic <see cref="IAsyncCodeLensDataPointProvider"/> supplying the own-level hook-match-count
/// CodeLens (Feature:/Scenario: lines) — the
/// counterpart to <see cref="StepHooksCodeLensDataPointProvider"/>, which renders the step-hooks
/// lens that shares a Scenario: line with it. Paired with <see cref="LineCodeLens.FeatureCodeLensTaggerProvider"/>,
/// which supplies the tag positions.
/// </summary>
/// <remarks>
/// Both providers are offered the line's one tag and each creates a data point only when the tag's
/// descriptor lists its own lens kind, then filters the server's response to that kind — this is the
/// same shape Roslyn uses to put "N references | N changes" on one C# member. See
/// <see cref="FeatureCodeLensTaggerProvider"/> for why every kind on a line shares one tag.
/// </remarks>
/// <remarks>
/// Runs out-of-process, in the CodeLens ServiceHub host — confirmed live via <c>tasklist</c> against
/// the PID that invoked this class (<c>ServiceHub.Host.netfx.Any</c>, not <c>devenv.exe</c>). It
/// cannot reach the LSP bridge via the static <see cref="HookCodeLensRedirect"/> (invisible across
/// the process boundary); data points import <see cref="ICodeLensCallbackService"/> and call back
/// into <see cref="HookCodeLensCallbackListener"/>, the in-proc listener, instead.
/// </remarks>
[Export(typeof(IAsyncCodeLensDataPointProvider))]
[Name(Id)]
[ContentType(VsWellKnownIds.GherkinContentType)]
// Lower Priority sorts first (Microsoft.VisualStudio.Utilities.PriorityAttribute) — 200 sits between
// Run (100, RunTestCodeLensDataPointProvider) and the step-hooks count (300,
// StepHooksCodeLensDataPointProvider).
[Priority(200)]
[LocalizedName(typeof(CodeLensResources), nameof(CodeLensResources.HookMatchCountProviderName))]
internal sealed class HookCodeLensDataPointProvider : IAsyncCodeLensDataPointProvider
{
    internal const string Id = "ReqnrollHookMatchCountCodeLensProvider";

    private readonly ICodeLensCallbackService _callbackService;

    [ImportingConstructor]
    public HookCodeLensDataPointProvider(ICodeLensCallbackService callbackService)
    {
        _callbackService = callbackService;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Accepts only descriptors whose line carries an own-level hooks lens. The host offers the
    /// line's tag to every provider (Run included), and a data point it creates keeps an
    /// indicator slot in the row even when <see cref="HookCodeLensDataPoint.GetDataAsync"/> returns
    /// an empty description — so declining here is what keeps a line without hooks from showing an
    /// empty entry. See <see cref="LineElementDescription"/>.
    /// </remarks>
    public Task<bool> CanCreateDataPointAsync(CodeLensDescriptor descriptor, CodeLensDescriptorContext descriptorContext, CancellationToken token) =>
        Task.FromResult(LineElementDescription.HasLensKind(descriptor.ElementDescription, LineLensKinds.Hooks));

    /// <inheritdoc />
    public Task<IAsyncCodeLensDataPoint> CreateDataPointAsync(CodeLensDescriptor descriptor, CodeLensDescriptorContext descriptorContext, CancellationToken token)
    {
        LineElementDescription.TryDecode(descriptor.ElementDescription, out var line);

        // descriptor.FilePath is a plain filesystem path; every other bridge call is keyed by the
        // file:// URI the LSP server uses, so convert once here.
        var fileUri = TryGetFileUri(descriptor.FilePath) ?? descriptor.FilePath;

        IAsyncCodeLensDataPoint dataPoint =
            new HookCodeLensDataPoint(descriptor, _callbackService, fileUri, line, isStepHooksLens: false);
        return Task.FromResult(dataPoint);
    }

    internal static string? TryGetFileUri(string filePath)
    {
        try
        {
            return new Uri(filePath).AbsoluteUri;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }
}
