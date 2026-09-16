using System.Xml;
using System.Xml.XPath;

namespace Reqnroll.IdeSupport.VisualStudio.TestLogger;

/// <summary>
/// Pure runsettings-document merge for registering the bundled Reqnroll VSTest logger
/// (<c>Reqnroll.IdeSupport.TestLogger.dll</c>) into a run. No VS dependencies — the MEF-exported
/// <see cref="ReqnrollTestLoggerRunSettingsService"/> is the thin host-facing shell around this.
/// </summary>
/// <remarks>
/// <para>
/// The input is whatever VS resolved as the run's effective runsettings: the user's
/// <c>.runsettings</c> file when one is selected (relative paths already fixed up by VS), otherwise
/// VS's synthesized default. Both halves of the registration are <em>merged</em> into it, never
/// overwritten:
/// </para>
/// <list type="bullet">
///   <item><c>/RunSettings/RunConfiguration/TestAdaptersPaths</c> — created if absent; otherwise the
///   logger directory is appended to the existing <c>;</c>-separated list (skipped if already
///   present) so a user's own adapter paths keep working.</item>
///   <item><c>/RunSettings/LoggerRunSettings/Loggers/Logger[@friendlyName='ReqnrollIde']</c> — created
///   with our <c>&lt;Configuration&gt;</c>; a pre-existing entry with our friendly name or URI is
///   replaced (ours is authoritative for its own parameters), other loggers are left alone.</item>
/// </list>
/// <para>
/// The input document is never mutated; the merged result is a fresh <see cref="XmlDocument"/>, which
/// also matches VS's own recovery contract — if a service throws, VS reverts to its pre-service
/// snapshot, so an in-place mutation that failed halfway would be silently discarded anyway.
/// </para>
/// </remarks>
internal static class TestLoggerRunSettings
{
    // Mirrors of ReqnrollIdeTestLogger's constants. Not referenced from that assembly on purpose: a
    // compile reference would copy Reqnroll.IdeSupport.TestLogger.dll into this project's output and
    // from there into the VSIX root, i.e. a second *TestLogger.dll for vstest to find.
    public const string LoggerFriendlyName = "ReqnrollIde";
    public const string LoggerExtensionUri = "logger://Reqnroll/IdeSupport/v1";
    public const string EndpointParameter = "Endpoint";
    public const string TokenParameter = "Token";
    public const string RunIdParameter = "RunId";
    public const string IdeProcessIdParameter = "IdeProcessId";
    public const string LogFilePathParameter = "LogFilePath";

    /// <summary>
    /// Set to <c>1</c> for the default mirror file under the Reqnroll log directory, or to an absolute
    /// path, to have the logger also append its NDJSON to a file (troubleshooting only).
    /// </summary>
    public const string MirrorFileEnvironmentVariable = "REQNROLL_IDE_TEST_LOGGER_MIRROR";

    /// <summary>File name of the bundled logger; the VSIX places it under <see cref="LoggerSubdirectory"/>.</summary>
    public const string LoggerAssemblyFileName = "Reqnroll.IdeSupport.TestLogger.dll";

    /// <summary>VSIX sub-path holding the logger (see the Extension csproj's ProjectReference VSIXSubPath).</summary>
    public const string LoggerSubdirectory = "TestLogger";

    /// <summary>
    /// Returns a copy of <paramref name="input"/> with the logger registration merged in.
    /// </summary>
    /// <param name="input">The effective runsettings VS handed to the service; may be an empty document.</param>
    /// <param name="loggerDirectory">Absolute directory containing <see cref="LoggerAssemblyFileName"/>.</param>
    /// <param name="loggerParameters">Children of the injected <c>&lt;Configuration&gt;</c> element, in order.</param>
    public static XmlDocument Inject(IXPathNavigable input, string loggerDirectory, IEnumerable<KeyValuePair<string, string>> loggerParameters)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));
        if (string.IsNullOrWhiteSpace(loggerDirectory)) throw new ArgumentException("Logger directory is required.", nameof(loggerDirectory));
        if (loggerParameters is null) throw new ArgumentNullException(nameof(loggerParameters));

        var doc = Clone(input);
        var runSettings = EnsureChild(doc, doc, "RunSettings");

        var runConfiguration = EnsureChild(doc, runSettings, "RunConfiguration");
        var adapterPaths = EnsureChild(doc, runConfiguration, "TestAdaptersPaths");
        adapterPaths.InnerText = AppendPath(adapterPaths.InnerText, loggerDirectory);

        var loggers = EnsureChild(doc, EnsureChild(doc, runSettings, "LoggerRunSettings"), "Loggers");
        RemoveExistingRegistration(loggers);

        var logger = doc.CreateElement("Logger");
        logger.SetAttribute("friendlyName", LoggerFriendlyName);
        logger.SetAttribute("enabled", "True");
        var configuration = doc.CreateElement("Configuration");
        foreach (var parameter in loggerParameters)
        {
            var element = doc.CreateElement(parameter.Key);
            element.InnerText = parameter.Value;
            configuration.AppendChild(element);
        }
        logger.AppendChild(configuration);
        loggers.AppendChild(logger);

        return doc;
    }

    /// <summary>True when <paramref name="doc"/> already carries our logger registration (used by tests and diagnostics).</summary>
    public static bool ContainsRegistration(IXPathNavigable doc)
        => doc.CreateNavigator().SelectSingleNode(RegistrationXPath) is not null;

    private const string RegistrationXPath =
        "/RunSettings/LoggerRunSettings/Loggers/Logger[@friendlyName='" + LoggerFriendlyName + "' or @uri='" + LoggerExtensionUri + "']";

    private static XmlDocument Clone(IXPathNavigable input)
    {
        var doc = new XmlDocument();
        var navigator = input.CreateNavigator();
        navigator.MoveToRoot();
        var xml = navigator.OuterXml;
        if (!string.IsNullOrWhiteSpace(xml))
            doc.LoadXml(xml);
        return doc;
    }

    private static XmlElement EnsureChild(XmlDocument doc, XmlNode parent, string name)
    {
        foreach (XmlNode child in parent.ChildNodes)
        {
            if (child is XmlElement element && element.LocalName == name && string.IsNullOrEmpty(element.NamespaceURI))
                return element;
        }
        var created = doc.CreateElement(name);
        parent.AppendChild(created);
        return created;
    }

    /// <summary>
    /// vstest splits <c>TestAdaptersPaths</c> on <c>;</c> (RunSettingsUtilities.GetTestAdaptersPaths).
    /// Existing entries are preserved verbatim; ours is appended unless an equivalent path is already
    /// listed (case-insensitive, trailing separator ignored — Windows paths).
    /// </summary>
    private static string AppendPath(string? existing, string loggerDirectory)
    {
        existing ??= string.Empty;
        var normalizedNew = Normalize(loggerDirectory);
        var parts = existing
            .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        // Already listed: hand the user's value back byte-for-byte rather than a re-joined copy.
        if (parts.Any(p => string.Equals(Normalize(p), normalizedNew, StringComparison.OrdinalIgnoreCase)))
            return existing;

        parts.Add(loggerDirectory);
        return string.Join(";", parts);

        static string Normalize(string path) => path.TrimEnd('\\', '/');
    }

    private static void RemoveExistingRegistration(XmlElement loggers)
    {
        var stale = loggers.ChildNodes
            .OfType<XmlElement>()
            .Where(e => e.LocalName == "Logger" &&
                        (string.Equals(e.GetAttribute("friendlyName"), LoggerFriendlyName, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(e.GetAttribute("uri"), LoggerExtensionUri, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        foreach (var element in stale)
            loggers.RemoveChild(element);
    }
}
