#nullable enable

namespace Reqnroll.IdeSupport.VisualStudio.HookCodeLens;

/// <summary>
/// One hook-match-count lens entry for a <c>.feature</c> line, as returned by the LSP server's
/// <c>textDocument/codeLens</c> (<c>HookCodeLensHandler</c>, issue #269) and consumed by the
/// classic-CodeLens bridge (issue #372).
/// </summary>
public sealed record HookFeatureLensEntry(
    /// <summary>0-based line the lens should render above.</summary>
    int Line,
    /// <summary>Display title, e.g. "2 hooks" or "1 step hook".</summary>
    string Title,
    /// <summary>0-based line the click action should query hooks for (may differ from <see cref="Line"/> — e.g. the step-hooks lens displays on the Scenario: line but targets the first step).</summary>
    int NavLine,
    /// <summary>0-based column of <see cref="NavLine"/>.</summary>
    int NavChar,
    /// <summary>Whether the click action should restrict results to hooks native to the resolved context level (server's <c>ownLevelOnly</c> flag).</summary>
    bool OwnLevelOnly,
    /// <summary>Whether the click action should always show the picker, even for a single match.</summary>
    bool AlwaysShowPicker);
