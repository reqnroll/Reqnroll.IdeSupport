using Reqnroll.IdeSupport.Common.Lsp;
using Reqnroll.IdeSupport.LSP.Server.Performance;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Performance;

/// <summary>Tests for <see cref="FeatureUsageCatalog"/> (issue #582).</summary>
public class FeatureUsageCatalogTests
{
    [Theory]
    [InlineData("textDocument/completion#step", "Completion.Step", FeatureUsageKind.Lookup)]
    [InlineData("textDocument/completion#keyword", "Completion.Keyword", FeatureUsageKind.Lookup)]
    [InlineData("textDocument/completion#tag", "Completion.Tag", FeatureUsageKind.Lookup)]
    [InlineData("textDocument/completion", "Completion.Other", FeatureUsageKind.Lookup)]
    [InlineData("textDocument/codeAction", "CodeAction", FeatureUsageKind.Lookup)]
    [InlineData("textDocument/codeLens", "CodeLens", FeatureUsageKind.Passive)]
    [InlineData("textDocument/inlayHint", "InlayHint", FeatureUsageKind.Passive)]
    [InlineData("textDocument/foldingRange", "FoldingRange", FeatureUsageKind.Passive)]
    [InlineData("textDocument/documentSymbol", "DocumentSymbol", FeatureUsageKind.Passive)]
    [InlineData("textDocument/onTypeFormatting", "OnTypeFormatting", FeatureUsageKind.Passive)]
    public void TryGet_maps_volume_operations_to_feature_keys(string operation, string key, FeatureUsageKind kind)
    {
        FeatureUsageCatalog.TryGet(operation, out var entry).Should().BeTrue();
        entry.Should().Be(new FeatureUsageEntry(key, kind));
    }

    [Fact]
    public void Both_document_symbol_requests_share_one_feature_key()
    {
        FeatureUsageCatalog.TryGet(LspStandardMethodNames.TextDocumentDocumentSymbol, out var flat).Should().BeTrue();
        FeatureUsageCatalog.TryGet(CustomLspMethodNames.ReqnrollDocumentSymbolHierarchical, out var hierarchical).Should().BeTrue();

        hierarchical.Key.Should().Be(flat.Key);
    }

    /// <summary>
    /// Every discrete command sends its own per-call telemetry event (#849); counting it here too would
    /// double-count each invocation. This pins the boundary between the two mechanisms.
    /// </summary>
    [Theory]
    [InlineData(LspStandardMethodNames.TextDocumentDefinition)]
    [InlineData(LspStandardMethodNames.TextDocumentReferences)]
    [InlineData(LspStandardMethodNames.TextDocumentRename)]
    [InlineData(LspStandardMethodNames.TextDocumentPrepareRename)]
    [InlineData(LspStandardMethodNames.TextDocumentFormatting)]
    [InlineData(LspStandardMethodNames.TextDocumentRangeFormatting)]
    [InlineData(CustomLspMethodNames.ReqnrollFindStepUsages)]
    [InlineData(CustomLspMethodNames.ReqnrollFindStepDefinitions)]
    [InlineData(CustomLspMethodNames.ReqnrollFindHooks)]
    [InlineData(CustomLspMethodNames.ReqnrollFindMatchingScenarios)]
    [InlineData(CustomLspMethodNames.ReqnrollFindUnusedStepDefinitions)]
    [InlineData(CustomLspMethodNames.ReqnrollRenameTargets)]
    [InlineData(CustomLspMethodNames.ReqnrollSelectRenameTarget)]
    [InlineData(CustomLspMethodNames.ReqnrollResolveTestTargets)]
    [InlineData(CustomLspMethodNames.ReqnrollResolveContainerTestTargets)]
    [InlineData("reqnroll.toggleComment")]
    public void IsCounted_is_false_for_discrete_commands_that_send_their_own_event(string operation)
        => FeatureUsageCatalog.IsCounted(operation).Should().BeFalse();

    [Theory]
    [InlineData(LspStandardMethodNames.TextDocumentSemanticTokensFull)]
    [InlineData(LspStandardMethodNames.TextDocumentDidChange)]
    [InlineData(LspStandardMethodNames.TextDocumentPublishDiagnostics)]
    [InlineData(LspStandardMethodNames.WorkspaceSemanticTokensRefresh)]
    [InlineData("internal/bindingRegistryReconcile")]
    public void IsCounted_is_false_for_plumbing_that_says_nothing_about_feature_use(string operation)
        => FeatureUsageCatalog.IsCounted(operation).Should().BeFalse();

    [Fact]
    public void Every_catalogued_key_has_exactly_one_kind()
    {
        var kindsByKey = FeatureUsageCatalog.Operations
            .Select(op => { FeatureUsageCatalog.TryGet(op, out var e); return e; })
            .GroupBy(e => e.Key, e => e.Kind);

        foreach (var group in kindsByKey)
            group.Distinct().Should().ContainSingle($"'{group.Key}' must not be reported as both Lookup and Passive");
    }

    [Fact]
    public void KindOf_returns_null_for_a_key_outside_the_catalogue()
        => FeatureUsageCatalog.KindOf("C:/Users/someone/secret.feature").Should().BeNull();

    [Theory]
    [InlineData(FeatureUsageCatalog.DirectKeys.TestRunRun, FeatureUsageKind.Passive)]
    [InlineData(FeatureUsageCatalog.DirectKeys.TestRunDebug, FeatureUsageKind.Passive)]
    [InlineData(FeatureUsageCatalog.DirectKeys.TestRunUnknown, FeatureUsageKind.Passive)]
    [InlineData(FeatureUsageCatalog.DirectKeys.UndefinedStepsPeak, FeatureUsageKind.Peak)]
    [InlineData(FeatureUsageCatalog.DirectKeys.AmbiguousStepsPeak, FeatureUsageKind.Peak)]
    [InlineData(FeatureUsageCatalog.DirectKeys.ParseErrorsPeak, FeatureUsageKind.Peak)]
    public void KindOf_knows_the_directly_written_keys(string key, FeatureUsageKind kind)
        => FeatureUsageCatalog.KindOf(key).Should().Be(kind);

    [Fact]
    public void Direct_keys_do_not_collide_with_operation_derived_keys()
    {
        var derived = FeatureUsageCatalog.Operations
            .Select(op => { FeatureUsageCatalog.TryGet(op, out var e); return e.Key; });

        derived.Should().NotIntersectWith(FeatureUsageCatalog.DirectCounterKeys);
    }

    [Fact]
    public void Run_and_debug_are_counted_through_registerRun_params_not_the_operation_label()
        => FeatureUsageCatalog.IsCounted(CustomLspMethodNames.ReqnrollRegisterTestRun).Should().BeFalse(
            "registerRun is also called once at VS Code activation; counting its label would count that");
}
