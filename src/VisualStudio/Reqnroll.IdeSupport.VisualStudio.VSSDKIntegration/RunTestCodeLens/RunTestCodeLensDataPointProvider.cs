#nullable enable

using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Language.CodeLens.Remoting;
using Microsoft.VisualStudio.Utilities;
using Reqnroll.IdeSupport.VisualStudio.LineCodeLens;
using Reqnroll.IdeSupport.VisualStudio.HookCodeLens;

namespace Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;

/// <summary>
/// Classic <see cref="IAsyncCodeLensDataPointProvider"/> supplying the Run CodeLens on
/// Scenario/Scenario Outline lines (design doc §5/§6, issue #262) — VS's own built-in test CodeLens
/// (<c>TestStatusProvider</c>, `Microsoft.VisualStudio.TestWindow.CodeLens.dll`) is itself a
/// provider in this exact API family, scoped to <c>CSharp</c>/<c>Basic</c>/<c>C/C++</c>; this is the
/// same shape, scoped to <c>Gherkin</c> instead. Paired with <see cref="RunTestCodeLensTaggerProvider"/>.
/// </summary>
/// <remarks>
/// Runs out-of-process, in the CodeLens ServiceHub host — same confirmed process boundary as
/// <c>HookCodeLensDataPointProvider</c> (issue #372). Reaches the LSP bridge via
/// <see cref="ICodeLensCallbackService"/> calling back into <see cref="RunTestCodeLensCallbackListener"/>,
/// not the static <see cref="RunTestCodeLensRedirect"/> (invisible across the process boundary).
/// </remarks>
[Export(typeof(IAsyncCodeLensDataPointProvider))]
[Name(Id)]
[ContentType("Gherkin")]
[Priority(200)]
[LocalizedName(typeof(CodeLensResources), nameof(CodeLensResources.RunTestProviderName))]
internal sealed class RunTestCodeLensDataPointProvider : IAsyncCodeLensDataPointProvider
{
    internal const string Id = "ReqnrollRunTestCodeLensProvider";

    private readonly ICodeLensCallbackService _callbackService;

    [ImportingConstructor]
    public RunTestCodeLensDataPointProvider(ICodeLensCallbackService callbackService)
    {
        _callbackService = callbackService;
    }

    /// <inheritdoc />
    /// <remarks>Only checks that the descriptor decodes structurally — whether the line actually has a resolved target is determined in <see cref="RunTestCodeLensDataPoint.GetDataAsync"/> via the callback round-trip.</remarks>
    public Task<bool> CanCreateDataPointAsync(CodeLensDescriptor descriptor, CodeLensDescriptorContext descriptorContext, CancellationToken token) =>
        Task.FromResult(LineElementDescription.TryDecode(descriptor.ElementDescription, out _));

    /// <inheritdoc />
    public Task<IAsyncCodeLensDataPoint> CreateDataPointAsync(CodeLensDescriptor descriptor, CodeLensDescriptorContext descriptorContext, CancellationToken token)
    {
        LineElementDescription.TryDecode(descriptor.ElementDescription, out var line);

        var fileUri = TryGetFileUri(descriptor.FilePath) ?? descriptor.FilePath;

        IAsyncCodeLensDataPoint dataPoint = new RunTestCodeLensDataPoint(descriptor, _callbackService, fileUri, line);
        return Task.FromResult(dataPoint);
    }

    private static string? TryGetFileUri(string filePath)
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
