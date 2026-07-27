namespace Reqnroll.IdeSupport.LSP.Server.Features.CodeLens;

/// <summary>
/// Payload for the <c>reqnroll/refreshCodeLens</c> server-to-client notification.
/// </summary>
/// <remarks>
/// The server pushes this to the Visual Studio client after a binding-registry replacement or an
/// incremental Roslyn patch (or a <c>.feature</c> edit that changes usage counts) so the VS client
/// can invalidate its already-rendered C# step code lenses and re-pull fresh usage counts. This is
/// the VS equivalent of the standard <c>workspace/codeLens/refresh</c> request, which VS cannot route
/// to our pipe-based code-lens provider. Other IDE clients use <c>workspace/codeLens/refresh</c>
/// instead and ignore this notification.
/// </remarks>
public sealed class RefreshCodeLensParams
{
    /// <summary>The project whose binding registry was replaced (informational; for diagnostics).</summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>
    /// <see langword="true"/> for a full binding-registry replacement (startup connector discovery,
    /// post-build, or membership-baseline arrival); <see langword="false"/> for an incremental
    /// refresh (a Roslyn patch on a <c>.cs</c> edit, or a <c>.feature</c> edit changing usage counts).
    /// </summary>
    /// <remarks>
    /// The VS client uses this to decide whether to call the VS SDK's <c>CodeLens.Invalidate()</c>
    /// (see <c>CodeLensRefreshInterceptor</c>'s remarks and issue #156/#318): that call was root-caused
    /// as the trigger for VS.Extensibility reactivating <c>ReqnrollLanguageClient</c> and forcing a
    /// second <c>CreateServerConnectionAsync</c>. It's only safe/worthwhile to eat that churn on a full
    /// replacement, which is comparatively rare; incremental refreshes are frequent (every settled
    /// edit) and would otherwise reproduce the same reconnect on a much tighter cadence.
    /// </remarks>
    public bool IsFullReplacement { get; set; }
}
