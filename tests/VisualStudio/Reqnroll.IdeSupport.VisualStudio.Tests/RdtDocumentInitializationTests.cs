using AwesomeAssertions;
using Microsoft.VisualStudio.Shell.Interop;
using Reqnroll.IdeSupport.VisualStudio.Extension;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests;

/// <summary>
/// Covers the rule <see cref="FeatureDocumentInitializationMonitor"/> applies to RDT
/// attribute-change notifications (issue #533, phase 2).
/// </summary>
public class RdtDocumentInitializationTests
{
    private const uint DocumentInitialized = (uint)__VSRDTATTRIB3.RDTA_DocumentInitialized;
    private const uint HierarchyInitialized = (uint)__VSRDTATTRIB3.RDTA_HierarchyInitialized;
    private const uint DocDataIsDirty = (uint)__VSRDTATTRIB.RDTA_DocDataIsDirty;

    [Fact]
    public void Reports_feature_document_initialization()
    {
        RdtDocumentInitialization
            .IsFeatureDocumentInitialization(@"C:\repo\Features\Login.feature", DocumentInitialized)
            .Should().BeTrue();
    }

    [Fact]
    public void Ignores_non_feature_documents()
    {
        RdtDocumentInitialization
            .IsFeatureDocumentInitialization(@"C:\repo\Steps\LoginSteps.cs", DocumentInitialized)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(DocDataIsDirty)]
    [InlineData(HierarchyInitialized)]
    [InlineData(0u)]
    public void Ignores_attribute_changes_that_are_not_initialization(uint attributes)
    {
        RdtDocumentInitialization
            .IsFeatureDocumentInitialization(@"C:\repo\Features\Login.feature", attributes)
            .Should().BeFalse();
    }

    [Fact]
    public void Reports_initialization_when_other_attributes_change_alongside_it()
    {
        RdtDocumentInitialization
            .IsFeatureDocumentInitialization(
                @"C:\repo\Features\Login.feature", DocumentInitialized | HierarchyInitialized)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(@"C:\repo\Features\Login.FEATURE")]
    [InlineData(@"C:\repo\Features\Login.Feature")]
    public void Matches_the_feature_extension_case_insensitively(string moniker)
    {
        RdtDocumentInitialization.IsFeatureDocumentInitialization(moniker, DocumentInitialized)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Tolerates_a_missing_moniker(string? moniker)
    {
        RdtDocumentInitialization.IsFeatureDocumentInitialization(moniker, DocumentInitialized)
            .Should().BeFalse();
    }

    [Fact]
    public void Does_not_match_a_file_merely_containing_the_word_feature()
    {
        RdtDocumentInitialization
            .IsFeatureDocumentInitialization(@"C:\repo\Features\feature.txt", DocumentInitialized)
            .Should().BeFalse();
    }
}
