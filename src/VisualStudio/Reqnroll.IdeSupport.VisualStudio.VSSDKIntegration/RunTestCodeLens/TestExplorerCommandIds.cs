#nullable enable

using System;

namespace Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;

/// <summary>
/// VS's own internal Test Explorer commands that a CodeLens data point's Details-popup
/// <c>CodeLensDetailPaneCommand</c> can invoke directly — decompiled from
/// <c>Microsoft.VisualStudio.TestWindow.CodeLens.dll</c>'s <c>TestStatusProvider</c> (design doc
/// §5). Letting Test Explorer itself do the run/debug means this extension needs no VS-specific
/// run-invocation logic at all — only the right <c>TestMethodIdentifier</c>.
/// </summary>
internal static class TestExplorerCommandIds
{
    /// <summary>Command group GUID shared by both commands below.</summary>
    internal static readonly Guid CommandSet = Guid.Parse("1E198C22-5980-4E7E-92F3-F73168D1FB63");

    /// <summary><c>.TestExplorer.RunTestsFromCodeLens</c>.</summary>
    internal const int RunCommandId = 898;

    /// <summary><c>.TestExplorer.DebugTestsFromCodeLens</c>.</summary>
    internal const int DebugCommandId = 899;
}
