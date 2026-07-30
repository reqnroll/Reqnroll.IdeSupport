#nullable enable

namespace Reqnroll.IdeSupport.VisualStudio.HookCodeLens;

/// <summary>One hook shown in a hook-match-count lens's Details popup (issue #372).</summary>
public sealed record HookDetailEntry(
    string HookType,
    string MethodName,
    int    HookOrder,
    string TargetUri,
    int    TargetLine,
    int    TargetChar);
