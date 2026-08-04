#nullable enable

using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Language.CodeLens.Remoting;
using Microsoft.VisualStudio.Utilities;

namespace Reqnroll.IdeSupport.VisualStudio.HookCodeLens;

/// <summary>
/// Classic <see cref="IAsyncCodeLensDataPointProvider"/> supplying the own-level hook-match-count
/// CodeLens (Feature:/Scenario: lines, issue #372, unblocking #269 for Visual Studio) — the
/// counterpart to <see cref="StepHooksCodeLensDataPointProvider"/>, which renders the step-hooks
/// lens that shares a Scenario: line with it. Paired with <see cref="HookCodeLensTaggerProvider"/>,
/// which supplies the tag positions.
/// </summary>
/// <remarks>
/// Both providers receive a data point for every tag and each filters the server's response to its
/// own lens kind, contributing no indicator when that kind is absent from the line — this is the
/// same shape Roslyn uses to put "N references | N changes" on one C# member. See
/// <see cref="HookElementDescription"/>'s remarks for why the two kinds cannot instead be split by
/// giving each its own tag.
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
[ContentType("gherkin")]
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
    /// Only checks that the descriptor decodes structurally. A descriptor identifies a line, not a
    /// lens, so this provider cannot tell in advance whether that line actually carries an own-level
    /// lens — that's resolved in <see cref="HookCodeLensDataPoint.GetDataAsync"/> via the callback
    /// round-trip, which contributes no indicator when the line has no lens of this kind.
    /// </remarks>
    public Task<bool> CanCreateDataPointAsync(CodeLensDescriptor descriptor, CodeLensDescriptorContext descriptorContext, CancellationToken token) =>
        Task.FromResult(HookElementDescription.TryDecode(descriptor.ElementDescription, out _));

    /// <inheritdoc />
    public Task<IAsyncCodeLensDataPoint> CreateDataPointAsync(CodeLensDescriptor descriptor, CodeLensDescriptorContext descriptorContext, CancellationToken token)
    {
        HookElementDescription.TryDecode(descriptor.ElementDescription, out var line);

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
