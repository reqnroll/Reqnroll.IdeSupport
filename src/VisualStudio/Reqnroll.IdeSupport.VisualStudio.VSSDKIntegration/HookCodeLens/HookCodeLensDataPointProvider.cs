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
/// Classic <see cref="IAsyncCodeLensDataPointProvider"/> supplying hook-match-count CodeLens data
/// for <c>reqnroll-gherkin</c> buffers (issue #372, unblocking #269 for Visual Studio). Paired with
/// <see cref="HookCodeLensTaggerProvider"/>, which supplies the tag positions.
/// </summary>
[Export(typeof(IAsyncCodeLensDataPointProvider))]
[Name(Id)]
[ContentType("reqnroll-gherkin")]
[Priority(200)]
internal sealed class HookCodeLensDataPointProvider : IAsyncCodeLensDataPointProvider
{
    internal const string Id = "ReqnrollHookMatchCountCodeLensProvider";

    /// <inheritdoc />
    public Task<bool> CanCreateDataPointAsync(CodeLensDescriptor descriptor, CodeLensDescriptorContext descriptorContext, CancellationToken token) =>
        Task.FromResult(HookCodeLensRedirect.GetLensesAsync is not null
            && HookElementDescription.TryDecode(descriptor.ElementDescription, out _, out _, out _, out _));

    /// <inheritdoc />
    public Task<IAsyncCodeLensDataPoint> CreateDataPointAsync(CodeLensDescriptor descriptor, CodeLensDescriptorContext descriptorContext, CancellationToken token)
    {
        HookElementDescription.TryDecode(descriptor.ElementDescription, out var line, out var navLine, out var navChar, out var ownLevelOnly);

        // descriptor.FilePath is a plain filesystem path; every other bridge call is keyed by the
        // file:// URI the LSP server uses, so convert once here.
        var fileUri = TryGetFileUri(descriptor.FilePath) ?? descriptor.FilePath;

        IAsyncCodeLensDataPoint dataPoint =
            new HookCodeLensDataPoint(descriptor, fileUri, line, navLine, navChar, ownLevelOnly);
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
