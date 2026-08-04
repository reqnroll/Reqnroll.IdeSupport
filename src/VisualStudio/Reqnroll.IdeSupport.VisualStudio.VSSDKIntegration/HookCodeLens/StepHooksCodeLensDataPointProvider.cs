#nullable enable
#pragma warning disable VSEXTPREVIEW_CODELENS

using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Language.CodeLens.Remoting;
using Microsoft.VisualStudio.Utilities;

namespace Reqnroll.IdeSupport.VisualStudio.HookCodeLens;

/// <summary>
/// Classic <see cref="IAsyncCodeLensDataPointProvider"/> supplying the step-hooks lens (the second
/// lens <c>HookCodeLensHandler.AddStepHooksLens</c> puts on a Scenario: line, for the scenario's
/// step-level hooks) — the counterpart to <see cref="HookCodeLensDataPointProvider"/>, which renders
/// every other hook-match-count lens. Two providers sharing one tag per line is what lets both
/// indicators appear on a Scenario: line at once; see <see cref="HookElementDescription"/>'s remarks.
/// </summary>
[Export(typeof(IAsyncCodeLensDataPointProvider))]
[Name(Id)]
[ContentType("gherkin")]
[Priority(200)]
[LocalizedName(typeof(CodeLensResources), nameof(CodeLensResources.StepHooksProviderName))]
internal sealed class StepHooksCodeLensDataPointProvider : IAsyncCodeLensDataPointProvider
{
    internal const string Id = "ReqnrollStepHooksCodeLensProvider";

    private readonly ICodeLensCallbackService _callbackService;

    [ImportingConstructor]
    public StepHooksCodeLensDataPointProvider(ICodeLensCallbackService callbackService)
    {
        _callbackService = callbackService;
    }

    /// <inheritdoc />
    /// <remarks>See <see cref="HookCodeLensDataPointProvider.CanCreateDataPointAsync"/>'s remarks — a
    /// descriptor identifies a line, so whether that line carries a step-hooks lens is resolved in
    /// <see cref="HookCodeLensDataPoint.GetDataAsync"/>, not here.</remarks>
    public Task<bool> CanCreateDataPointAsync(CodeLensDescriptor descriptor, CodeLensDescriptorContext descriptorContext, CancellationToken token) =>
        Task.FromResult(HookElementDescription.TryDecode(descriptor.ElementDescription, out _));

    /// <inheritdoc />
    public Task<IAsyncCodeLensDataPoint> CreateDataPointAsync(CodeLensDescriptor descriptor, CodeLensDescriptorContext descriptorContext, CancellationToken token)
    {
        HookElementDescription.TryDecode(descriptor.ElementDescription, out var line);

        var fileUri = HookCodeLensDataPointProvider.TryGetFileUri(descriptor.FilePath) ?? descriptor.FilePath;

        IAsyncCodeLensDataPoint dataPoint =
            new HookCodeLensDataPoint(descriptor, _callbackService, fileUri, line, isStepHooksLens: true);
        return Task.FromResult(dataPoint);
    }
}
