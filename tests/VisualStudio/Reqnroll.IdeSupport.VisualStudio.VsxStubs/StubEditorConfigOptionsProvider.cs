using Reqnroll.IdeSupport.Common.Configuration;

namespace Reqnroll.VisualStudio.VsxStubs;

public class StubEditorConfigOptionsProvider : IEditorConfigOptionsProvider
{
    public IEditorConfigOptions GetEditorConfigOptionsByPath(string filePath)
        => NullEditorConfigOptions.Instance;

    public void InvalidateCache(string editorConfigFilePath) { }
}
