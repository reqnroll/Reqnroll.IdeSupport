#nullable enable

using System.Collections.Generic;
using Reqnroll.IdeSupport.VisualStudio.Extension.Navigation;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.GoToHooks;

/// <summary>
/// Parsed result of a <c>reqnroll/findHooks</c> response.
/// </summary>
internal sealed class FindHooksResult
{
    /// <summary>Sentinel for a position with no applicable hooks.</summary>
    public static readonly FindHooksResult Empty = new(new List<HookLocation>());

    /// <summary>The applicable hook locations, in server-returned order.</summary>
    public IReadOnlyList<HookLocation> Hooks { get; }

    /// <summary>Creates a result wrapping the given hook locations.</summary>
    public FindHooksResult(IReadOnlyList<HookLocation> hooks)
    {
        Hooks = hooks;
    }
}

/// <summary>One applicable hook binding returned by the server.</summary>
internal sealed class HookLocation
{
    /// <summary>The document URI of the source file declaring the hook.</summary>
    public string Uri        { get; }
    /// <summary>0-based start line of the hook method declaration.</summary>
    public int    StartLine  { get; }
    /// <summary>0-based start character of the hook method declaration.</summary>
    public int    StartChar  { get; }
    /// <summary>The hook attribute type (e.g. <c>"BeforeScenario"</c>).</summary>
    public string HookType   { get; }
    /// <summary>The hook's execution order.</summary>
    public int    HookOrder  { get; }
    /// <summary>Name of the hook method.</summary>
    public string MethodName { get; }
    /// <summary>
    /// Whether <see cref="Uri"/> names a file on this machine. <see langword="false"/> for a hook
    /// returned only because the request asked for unresolved hooks; it is opened by
    /// <see cref="MethodIdentity"/> instead.
    /// </summary>
    public bool IsResolved { get; }
    /// <summary>The exact identity of the hook method, when the server reports one.</summary>
    public BindingMethodIdentity? MethodIdentity { get; }

    /// <summary>Creates a hook location from server-supplied coordinates and metadata.</summary>
    public HookLocation(
        string uri, int startLine, int startChar,
        string hookType, int hookOrder, string methodName,
        bool isResolved = true, BindingMethodIdentity? methodIdentity = null)
    {
        IsResolved     = isResolved;
        MethodIdentity = methodIdentity;
        Uri        = uri;
        StartLine  = startLine;
        StartChar  = startChar;
        HookType   = hookType;
        HookOrder  = hookOrder;
        MethodName = methodName;
    }
}
