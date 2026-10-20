namespace Reqnroll.IdeSupport.LSP.Server.Performance;

public static class InternalLspMethodNames
{

    // ── Internal Pipeline Operations (not on the wire; perf-recorder labels only) ──
    /// <summary>Internal perf-recorder label for binding-registry reconciliation after a connector update.</summary>
    public const string InternalBindingRegistryReconcile = "internal/bindingRegistryReconcile";
    /// <summary>Internal perf-recorder label for reconciliation triggered by a <c>reqnroll.json</c> change.</summary>
    public const string InternalReqnrollConfigReconcile = "internal/reqnrollConfigReconcile";
    /// <summary>Internal perf-recorder label for a debounced feature-file rescan.</summary>
    public const string InternalFeatureRescan = "internal/featureRescan";
    /// <summary>
    /// Internal perf-recorder label for a rename's post-response apply: the Visual Studio
    /// <c>workspace/applyEdit</c> round trip plus the cache commit it confirms. This work runs after
    /// the <c>textDocument/rename</c> response is sent, outside the measured request, so without
    /// its own label its cost would not appear anywhere.
    /// </summary>
    public const string InternalRenamePostResponseApply = "internal/renamePostResponseApply";
}
