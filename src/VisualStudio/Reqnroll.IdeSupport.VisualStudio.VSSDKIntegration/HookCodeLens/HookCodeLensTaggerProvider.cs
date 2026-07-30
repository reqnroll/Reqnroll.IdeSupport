#nullable enable

using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace Reqnroll.IdeSupport.VisualStudio.HookCodeLens;

/// <summary>
/// Classic MEF <see cref="ITaggerProvider"/> supplying <see cref="ICodeLensTag"/>s for
/// <c>reqnroll-gherkin</c> buffers (hook-match-count CodeLens — issue #372, unblocking #269 for
/// Visual Studio). Content-type scoped, like <c>GherkinDropdownBarTextViewCreationListener</c> —
/// no code-element/Roslyn model is needed for this API, unlike VS.Extensibility's
/// <c>ICodeLensProvider</c>.
/// </summary>
[Export(typeof(ITaggerProvider))]
[ContentType("reqnroll-gherkin")]
[TagType(typeof(ICodeLensTag))]
internal sealed class HookCodeLensTaggerProvider : ITaggerProvider
{
    /// <inheritdoc />
    public ITagger<T>? CreateTagger<T>(ITextBuffer buffer) where T : ITag
    {
        if (typeof(T) != typeof(ICodeLensTag))
            return null;

        if (!buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc))
            return null;

        string fileUri;
        try
        {
            fileUri = new Uri(doc.FilePath).AbsoluteUri;
        }
        catch (UriFormatException)
        {
            return null;
        }

        return buffer.Properties.GetOrCreateSingletonProperty(
            typeof(HookCodeLensTagger),
            () => new HookCodeLensTagger(buffer, doc.FilePath, fileUri)) as ITagger<T>;
    }
}
