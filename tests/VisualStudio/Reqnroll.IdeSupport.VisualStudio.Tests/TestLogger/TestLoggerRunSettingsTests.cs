using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio.TestLogger;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.TestLogger;

/// <summary>
/// Merge rules of <see cref="TestLoggerRunSettings.Inject"/> — the pure half of the
/// <c>IRunSettingsService</c> export that registers the bundled VSTest logger for Test Explorer runs.
/// The shapes fed in mirror what VS actually hands the service: its synthesized default document
/// when no runsettings file is selected, or the user's own file otherwise.
/// </summary>
public class TestLoggerRunSettingsTests
{
    private const string LoggerDir = @"C:\Ext\Reqnroll\TestLogger";

    private static readonly KeyValuePair<string, string>[] Parameters =
    {
        new(TestLoggerRunSettings.LogFilePathParameter, @"C:\Users\me\AppData\Local\Reqnroll\reqnroll-vs-testlogger-20260916-1234.log"),
        new(TestLoggerRunSettings.IdeProcessIdParameter, "1234"),
    };

    // Exactly what Microsoft.VisualStudio.TestWindow.Controller.RunsettingsProvider.CreateDefaultRunSettings builds.
    private const string VsDefaultDocument =
        "<?xml version=\"1.0\"?><RunSettings><DataCollectionRunSettings><DataCollectors></DataCollectors></DataCollectionRunSettings></RunSettings>";

    private static XmlDocument Load(string xml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        return doc;
    }

    private static string? Text(XmlDocument doc, string xpath) => doc.SelectSingleNode(xpath)?.InnerText;

    private static XmlElement[] LoggerElements(XmlDocument doc)
        => doc.SelectNodes("/RunSettings/LoggerRunSettings/Loggers/Logger")!.OfType<XmlElement>().ToArray();

    [Fact]
    public void Inject_into_VS_default_document_creates_both_halves_and_keeps_existing_content()
    {
        var input = Load(VsDefaultDocument);

        var result = TestLoggerRunSettings.Inject(input, LoggerDir, Parameters);

        Text(result, "/RunSettings/RunConfiguration/TestAdaptersPaths").Should().Be(LoggerDir);

        var loggers = LoggerElements(result);
        loggers.Should().ContainSingle();
        loggers[0].GetAttribute("friendlyName").Should().Be(TestLoggerRunSettings.LoggerFriendlyName);
        loggers[0].GetAttribute("enabled").Should().Be("True");
        Text(result, "/RunSettings/LoggerRunSettings/Loggers/Logger/Configuration/LogFilePath").Should().Be(Parameters[0].Value);
        Text(result, "/RunSettings/LoggerRunSettings/Loggers/Logger/Configuration/IdeProcessId").Should().Be("1234");

        // VS's own default content survives the merge.
        result.SelectSingleNode("/RunSettings/DataCollectionRunSettings/DataCollectors").Should().NotBeNull();
    }

    [Fact]
    public void Inject_appends_to_existing_TestAdaptersPaths_instead_of_replacing()
    {
        var input = Load("<RunSettings><RunConfiguration><TestAdaptersPaths>C:\\UserAdapters;D:\\More</TestAdaptersPaths><MaxCpuCount>1</MaxCpuCount></RunConfiguration></RunSettings>");

        var result = TestLoggerRunSettings.Inject(input, LoggerDir, Parameters);

        Text(result, "/RunSettings/RunConfiguration/TestAdaptersPaths").Should().Be($"C:\\UserAdapters;D:\\More;{LoggerDir}");
        Text(result, "/RunSettings/RunConfiguration/MaxCpuCount").Should().Be("1", "unrelated RunConfiguration children must be untouched");
    }

    [Theory]
    [InlineData(@"C:\Ext\Reqnroll\TestLogger")]
    [InlineData(@"c:\ext\reqnroll\testlogger")]
    [InlineData(@"C:\Ext\Reqnroll\TestLogger\")]
    [InlineData(@" C:\Ext\Reqnroll\TestLogger ; D:\Other ")]
    public void Inject_does_not_duplicate_an_already_listed_logger_directory(string existing)
    {
        var input = Load($"<RunSettings><RunConfiguration><TestAdaptersPaths>{existing}</TestAdaptersPaths></RunConfiguration></RunSettings>");

        var result = TestLoggerRunSettings.Inject(input, LoggerDir, Parameters);

        var paths = Text(result, "/RunSettings/RunConfiguration/TestAdaptersPaths")!
            .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().TrimEnd('\\'));
        paths.Count(p => string.Equals(p, LoggerDir, StringComparison.OrdinalIgnoreCase)).Should().Be(1);
    }

    [Fact]
    public void Inject_preserves_other_loggers_the_user_configured()
    {
        var input = Load("<RunSettings><LoggerRunSettings><Loggers><Logger friendlyName=\"trx\" enabled=\"True\"><Configuration><LogFileName>out.trx</LogFileName></Configuration></Logger></Loggers></LoggerRunSettings></RunSettings>");

        var result = TestLoggerRunSettings.Inject(input, LoggerDir, Parameters);

        var loggers = LoggerElements(result);
        loggers.Select(l => l.GetAttribute("friendlyName")).Should().Equal("trx", TestLoggerRunSettings.LoggerFriendlyName);
        Text(result, "/RunSettings/LoggerRunSettings/Loggers/Logger[@friendlyName='trx']/Configuration/LogFileName").Should().Be("out.trx");
    }

    [Theory]
    [InlineData("friendlyName=\"ReqnrollIde\"")]
    [InlineData("friendlyName=\"reqnrollide\"")]
    [InlineData("uri=\"logger://Reqnroll/IdeSupport/v1\"")]
    public void Inject_replaces_a_stale_registration_of_our_own_logger(string identifyingAttribute)
    {
        var input = Load($"<RunSettings><LoggerRunSettings><Loggers><Logger {identifyingAttribute} enabled=\"True\"><Configuration><LogFilePath>C:\\stale.log</LogFilePath></Configuration></Logger></Loggers></LoggerRunSettings></RunSettings>");

        var result = TestLoggerRunSettings.Inject(input, LoggerDir, Parameters);

        var loggers = LoggerElements(result);
        loggers.Should().ContainSingle();
        Text(result, "/RunSettings/LoggerRunSettings/Loggers/Logger/Configuration/LogFilePath").Should().Be(Parameters[0].Value);
    }

    [Fact]
    public void Inject_leaves_the_input_document_unmodified()
    {
        var input = Load(VsDefaultDocument);
        var before = input.OuterXml;

        TestLoggerRunSettings.Inject(input, LoggerDir, Parameters);

        input.OuterXml.Should().Be(before);
    }

    [Fact]
    public void Inject_handles_an_empty_document_by_creating_the_RunSettings_root()
    {
        var result = TestLoggerRunSettings.Inject(new XmlDocument(), LoggerDir, Parameters);

        result.DocumentElement!.Name.Should().Be("RunSettings");
        Text(result, "/RunSettings/RunConfiguration/TestAdaptersPaths").Should().Be(LoggerDir);
        LoggerElements(result).Should().ContainSingle();
    }

    [Fact]
    public void Inject_accepts_a_navigator_positioned_away_from_the_root()
    {
        // VS feeds each service the previous service's return value, which is often an XPathNavigator
        // (not an XmlDocument) left wherever that service stopped.
        var input = Load("<RunSettings><RunConfiguration><MaxCpuCount>2</MaxCpuCount></RunConfiguration></RunSettings>");
        var navigator = input.CreateNavigator()!;
        navigator.MoveToFollowing("MaxCpuCount", string.Empty);

        var result = TestLoggerRunSettings.Inject(navigator, LoggerDir, Parameters);

        Text(result, "/RunSettings/RunConfiguration/MaxCpuCount").Should().Be("2");
        Text(result, "/RunSettings/RunConfiguration/TestAdaptersPaths").Should().Be(LoggerDir);
    }

    [Fact]
    public void Inject_xml_escapes_parameter_values_and_paths()
    {
        var dir = @"C:\Ext\R&D <spike>\TestLogger";
        var parameters = new[] { new KeyValuePair<string, string>(TestLoggerRunSettings.LogFilePathParameter, @"C:\logs\a&b.log") };

        var result = TestLoggerRunSettings.Inject(Load(VsDefaultDocument), dir, parameters);

        // Round-trip through a fresh parse proves the serialized XML is well-formed and the values intact.
        var reparsed = Load(result.OuterXml);
        Text(reparsed, "/RunSettings/RunConfiguration/TestAdaptersPaths").Should().Be(dir);
        Text(reparsed, "/RunSettings/LoggerRunSettings/Loggers/Logger/Configuration/LogFilePath").Should().Be(@"C:\logs\a&b.log");
    }

    [Fact]
    public void ContainsRegistration_reflects_whether_our_logger_is_present()
    {
        var input = Load(VsDefaultDocument);
        TestLoggerRunSettings.ContainsRegistration(input).Should().BeFalse();

        var result = TestLoggerRunSettings.Inject(input, LoggerDir, Parameters);

        TestLoggerRunSettings.ContainsRegistration(result).Should().BeTrue();
    }

    [Fact]
    public void Inject_rejects_missing_arguments()
    {
        var doc = Load(VsDefaultDocument);

        FluentActions.Invoking(() => TestLoggerRunSettings.Inject(null!, LoggerDir, Parameters)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => TestLoggerRunSettings.Inject(doc, " ", Parameters)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => TestLoggerRunSettings.Inject(doc, LoggerDir, null!)).Should().Throw<ArgumentNullException>();
    }
}
