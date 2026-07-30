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
/// step-level hooks) — the counterpart to <see cref="HookCodeLensDataPointProvider"/>, which claims
/// every other hook-match-count entry. See <see cref="HookCodeLensDataPointProvider"/>'s remarks for
/// why the two lens kinds need separate providers rather than one provider handling both.
/// </summary>
[Export(typeof(IAsyncCodeLensDataPointProvider))]
[Name(Id)]
[ContentType("reqnroll-gherkin")]
[Priority(200)]
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
    public Task<bool> CanCreateDataPointAsync(CodeLensDescriptor descriptor, CodeLensDescriptorContext descriptorContext, CancellationToken token) =>
        Task.FromResult(
            HookElementDescription.TryDecode(descriptor.ElementDescription, out _, out _, out _, out _, out var isStepHooksLens)
            && isStepHooksLens);

    /// <inheritdoc />
    public Task<IAsyncCodeLensDataPoint> CreateDataPointAsync(CodeLensDescriptor descriptor, CodeLensDescriptorContext descriptorContext, CancellationToken token)
    {
        HookElementDescription.TryDecode(descriptor.ElementDescription, out var line, out var navLine, out var navChar, out var ownLevelOnly, out _);

        var fileUri = HookCodeLensDataPointProvider.TryGetFileUri(descriptor.FilePath) ?? descriptor.FilePath;

        IAsyncCodeLensDataPoint dataPoint =
            new HookCodeLensDataPoint(descriptor, _callbackService, fileUri, line, navLine, navChar, ownLevelOnly);
        return Task.FromResult(dataPoint);
    }
}
