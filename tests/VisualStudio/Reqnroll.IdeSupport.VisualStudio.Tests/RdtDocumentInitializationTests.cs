using AwesomeAssertions;
using Microsoft.VisualStudio.Shell.Interop;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.VisualStudio.Extension;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests;

/// <summary>
/// Covers the rules <see cref="DocumentInitializationMonitor"/> applies to RDT monikers and
/// attribute-change notifications (issue #533, phase 2).
/// </summary>
public class RdtDocumentInitializationTests
{
    private const uint DocumentInitialized = (uint)__VSRDTATTRIB3.RDTA_DocumentInitialized;
    private const uint HierarchyInitialized = (uint)__VSRDTATTRIB3.RDTA_HierarchyInitialized;
    private const uint DocDataIsDirty = (uint)__VSRDTATTRIB.RDTA_DocDataIsDirty;

    private const string Feature = @"C:\repo\Features\Login.feature";
    private const string Steps = @"C:\repo\StepDefinitions\LoginSteps.cs";

    [Theory]
    [InlineData(Feature)]
    [InlineData(Steps)]
    public void Reports_initialization_for_named_documents(string moniker)
    {
        RdtDocumentInitialization.IsDocumentInitialization(moniker, DocumentInitialized)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(DocDataIsDirty)]
    [InlineData(HierarchyInitialized)]
    [InlineData(0u)]
    public void Ignores_attribute_changes_that_are_not_initialization(uint attributes)
    {
        RdtDocumentInitialization.IsDocumentInitialization(Feature, attributes)
            .Should().BeFalse();
    }

    [Fact]
    public void Reports_initialization_when_other_attributes_change_alongside_it()
    {
        RdtDocumentInitialization
            .IsDocumentInitialization(Feature, DocumentInitialized | HierarchyInitialized)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Tolerates_a_missing_moniker(string? moniker)
    {
        RdtDocumentInitialization.IsDocumentInitialization(moniker, DocumentInitialized)
            .Should().BeFalse();
        RdtDocumentInitialization.Classify(moniker).ToString().Should().Be(nameof(RdtDocumentKind.Other));
        RdtDocumentInitialization.IsActivationRelevant(moniker).Should().BeFalse();
    }

    // The expected kind travels as a string: RdtDocumentKind is internal, so it cannot appear in
    // the signature of a public xUnit test method. nameof keeps it compile-checked all the same.
    [Theory]
    [InlineData(@"C:\repo\Features\Login.feature", nameof(RdtDocumentKind.Feature))]
    [InlineData(@"C:\repo\Features\Login.FEATURE", nameof(RdtDocumentKind.Feature))]
    [InlineData(@"C:\repo\StepDefinitions\LoginSteps.cs", nameof(RdtDocumentKind.CSharp))]
    [InlineData(@"C:\repo\StepDefinitions\LoginSteps.CS", nameof(RdtDocumentKind.CSharp))]
    [InlineData(@"C:\repo\readme.md", nameof(RdtDocumentKind.Other))]
    [InlineData(@"C:\repo\Features\feature.txt", nameof(RdtDocumentKind.Other))]
    [InlineData(@"C:\repo\App.csproj", nameof(RdtDocumentKind.Other))]
    public void Classifies_documents_by_extension(string moniker, string expected)
    {
        RdtDocumentInitialization.Classify(moniker).ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData(Feature, true)]
    [InlineData(Steps, true)]
    [InlineData(@"C:\repo\readme.md", false)]
    public void Treats_feature_and_csharp_documents_as_activation_relevant(string moniker, bool expected)
    {
        RdtDocumentInitialization.IsActivationRelevant(moniker).Should().Be(expected);
    }

    [Fact]
    public void LogDocumentEvent_logs_a_stale_cookie_ArgumentException_at_Verbose_not_Error()
    {
        // Issue #814: a document released/closed during restore leaves a stale RDT cookie, and
        // GetDocumentMoniker throws ArgumentException for it. In an observe-only monitor that is
        // an expected race, so it must be logged at Verbose — never surface as a spurious [Error].
        var rdt4 = (IVsRunningDocumentTable4)Substitute.For<IVsRunningDocumentTable, IVsRunningDocumentTable4>();
        rdt4.GetDocumentMoniker(Arg.Any<uint>())
            .Returns(_ => throw new ArgumentException("Value does not fall within the expected range."));

        var logger = Substitute.For<IIdeSupportLogger>();
        logger.Level.Returns(TraceLevel.Verbose);
        var logged = new List<LogMessage>();
        logger.Log(Arg.Do<LogMessage>(m => logged.Add(m)));

        var monitor = CreateMonitor((IVsRunningDocumentTable)rdt4, logger);

        monitor.OnAfterDocumentWindowHide(1234, null);

        logged.Should().ContainSingle(m => m.Level == TraceLevel.Verbose)
            .Which.Message.Should().Contain("stale RDT cookie");
        logged.Should().NotContain(m => m.Level == TraceLevel.Error);
    }

    private static DocumentInitializationMonitor CreateMonitor(IVsRunningDocumentTable rdt, IIdeSupportLogger logger)
    {
        // The constructor is private; the monitor is only reachable through TryAdvise, which needs
        // a UI thread. Construct it directly via reflection so the exception path is testable.
        var ctor = typeof(DocumentInitializationMonitor).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            new[] { typeof(IVsRunningDocumentTable), typeof(IIdeSupportLogger) },
            null);
        return (DocumentInitializationMonitor)ctor!.Invoke(new object[] { rdt, logger });
    }
}
