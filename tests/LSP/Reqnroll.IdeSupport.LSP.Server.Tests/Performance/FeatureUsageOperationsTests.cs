using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Performance;

/// <summary>Spot checks for the discrete-command allowlist in <see cref="FeatureUsageOperations"/> (issue #582).</summary>
public class FeatureUsageOperationsTests
{
    [Theory]
    [MemberData(nameof(CountedOperations))]
    public void IsCounted_is_true_for_discrete_commands(string operation)
        => FeatureUsageOperations.IsCounted(operation).Should().BeTrue();

    public static IEnumerable<object[]> CountedOperations() => new[]
    {
        new object[] { LspMethodNames.TextDocumentDefinition },
        new object[] { LspMethodNames.TextDocumentReferences },
        new object[] { LspMethodNames.ReqnrollFindStepUsages },
        new object[] { LspMethodNames.TextDocumentRename },
        new object[] { LspMethodNames.TextDocumentCodeAction },
        new object[] { "reqnroll.toggleComment" },
    };

    [Theory]
    [InlineData("textDocument/completion")]
    [InlineData("textDocument/completion#keyword")]
    [InlineData("textDocument/completion#step")]
    [InlineData("textDocument/semanticTokens/full")]
    [InlineData("textDocument/didChange")]
    [InlineData("workspace/semanticTokens/refresh")]
    [InlineData("internal/bindingRegistryReconcile")]
    public void IsCounted_is_false_for_continuous_or_internal_operations(string operation)
        => FeatureUsageOperations.IsCounted(operation).Should().BeFalse();
}
