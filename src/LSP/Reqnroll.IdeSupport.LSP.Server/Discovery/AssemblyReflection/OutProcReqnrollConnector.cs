#nullable disable
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.Common.ProjectSystem.Settings;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Connector.Models;
using Reqnroll.IdeSupport.LSP.Server.Hosting;

namespace Reqnroll.IdeSupport.LSP.Server.Discovery;

/// <summary>Base class for connectors that run Reqnroll binding discovery in a separate out-of-process worker and deserialize its result.</summary>
public abstract class OutProcReqnrollConnector
{
    private const string BindingDiscoveryCommandName = "binding discovery";

    /// <summary>The Deveroom/Reqnroll configuration for the project being discovered.</summary>
    protected readonly IdeSupportConfiguration _configuration;
    /// <summary>Root folder of the IDE extension, used to locate bundled connector executables.</summary>
    protected readonly string _extensionFolder;
    /// <summary>Logger used to record connector invocation and diagnostic output.</summary>
    protected readonly IIdeSupportLogger _logger;
    /// <summary>Telemetry sink for discovery-run metrics; currently unused by the LSP server (see <see cref="Deserialize"/>).</summary>
    protected readonly ITelemetryService _telemetryService;
    /// <summary>Processor architecture to use when selecting the .NET Framework install location.</summary>
    protected readonly ProcessorArchitectureSetting _processorArchitecture;
    /// <summary>Settings of the project whose bindings are being discovered.</summary>
    protected readonly ProjectSettings _projectSettings;
    /// <summary>Target framework moniker of the project whose bindings are being discovered.</summary>
    protected readonly TargetFrameworkMoniker _targetFrameworkMoniker;
    /// <summary>The Reqnroll NuGet package version referenced by the project.</summary>
    protected NuGetVersion ReqnrollVersion => _projectSettings.ReqnrollVersion;

    /// <summary>Initializes the connector's shared configuration, logging, and project settings.</summary>
    protected OutProcReqnrollConnector(IdeSupportConfiguration configuration, IIdeSupportLogger logger,
        TargetFrameworkMoniker targetFrameworkMoniker, string extensionFolder,
        ProcessorArchitectureSetting processorArchitecture, ProjectSettings projectSettings,
        ITelemetryService telemetryService)
    {
        _configuration = configuration;
        _logger = logger;
        _targetFrameworkMoniker = targetFrameworkMoniker;
        _extensionFolder = extensionFolder;
        _processorArchitecture = processorArchitecture;
        _projectSettings = projectSettings;
        _telemetryService = telemetryService;
    }

    private bool DebugConnector => _configuration.DebugConnector ||
                                   Environment.GetEnvironmentVariable("DEVEROOM_DEBUGCONNECTOR") == "1";

    /// <summary>Derives a short connector-type name for telemetry/diagnostics by stripping the base class name suffix from the derived type's name.</summary>
    protected virtual string GetConnectorType()
    {
        return GetType().Name.Replace(nameof(OutProcReqnrollConnector), "");
    }

    /// <summary>Launches the out-of-process connector to run binding discovery against the given test assembly and returns the deserialized result.</summary>
    public virtual DiscoveryResult RunDiscovery(string testAssemblyPath, string configFilePath)
    {
        var workingDirectory = Path.GetDirectoryName(testAssemblyPath);
        var arguments = new List<string>();
        var connectorPath = GetConnectorPath(arguments);
        arguments.Add("discovery");
        arguments.Add(testAssemblyPath);
        arguments.Add(configFilePath);
        if (DebugConnector)
            arguments.Add("--debug");

        // A bare command name (e.g. "dotnet", from GetDotNetCommand()'s non-Windows PATH-resolution
        // fallback) has no directory component and is meant to be resolved by the OS via PATH when
        // the process actually launches — File.Exists can't check that (it only resolves relative
        // to the current directory) and would always report it missing even when it isn't. Only
        // pre-validate paths that look like real file paths; a genuinely-missing bare command still
        // surfaces a clear failure from ProcessHelper.RunProcess when the launch itself fails.
        var looksLikeFilePath = connectorPath != null && !string.IsNullOrEmpty(Path.GetDirectoryName(connectorPath));
        if (connectorPath == null || (looksLikeFilePath && !File.Exists(connectorPath)))
            return new DiscoveryResult
            {
                ErrorMessage = $"Error during binding discovery. Unable to find connector: {connectorPath}",
                TelemetryProperties = new Dictionary<string, object>(),
                ConnectorType = GetConnectorType()
            };

        var result = ProcessHelper.RunProcess(workingDirectory, connectorPath, arguments, encoding: Encoding.UTF8);

        _logger.LogVerbose($"{workingDirectory}>{connectorPath} {string.Join(" ", arguments)}");
        _logger.LogVerbose($"Exit code: {result.ExitCode}");
        if (result.HasErrors)
            _logger.LogWarning(result.StandardError);

#if DEBUG
        // Log only the JSON payload between the >>>>>>>>>> / <<<<<<<<<< markers; the assembly-loader
        // trace that precedes it is noise that bloats the log and buries the actual binding data.
        var jsonPayload = ExtractJsonPayload(result.StandardOut);
        if (jsonPayload != null)
            _logger.LogVerbose($"[Connector JSON]\n{jsonPayload}");
        else if (!string.IsNullOrWhiteSpace(result.StandardOut))
            _logger.LogVerbose($"[Connector stdout]\n{result.StandardOut}");
#endif

        DiscoveryResult discoveryResult;
        if (result.ExitCode != 0)
        {
            var errorMessage = result.HasErrors ? result.StandardError : "Unknown error.";

            discoveryResult = Deserialize(
                result,
                dr => GetDetailedErrorMessage(result, errorMessage + dr.ErrorMessage, BindingDiscoveryCommandName));
        }
        else
        {
            discoveryResult = Deserialize(
                result,
                dr => dr.IsFailed ? GetDetailedErrorMessage(result, dr.ErrorMessage, BindingDiscoveryCommandName) : dr.ErrorMessage!);
        }

        discoveryResult.ConnectorType = GetConnectorType();
        return discoveryResult;
    }

    private DiscoveryResult Deserialize(ProcessHelper.RunProcessResult result,
        Func<DiscoveryResult, string> formatErrorMessage)
    {
        DiscoveryResult discoveryResult;
        try
        {
            discoveryResult = ConnectorJsonSerialization.DeserializeObjectWithMarker<DiscoveryResult>(result.StandardOut)
                              ?? new DiscoveryResult
                              {
                                  ErrorMessage = $"Cannot deserialize: {result.StandardOut}",
                                  ConnectorType = GetConnectorType()
                              };
        }
        catch (Exception e)
        {
            discoveryResult = new DiscoveryResult
            {
                ErrorMessage = e.ToString(),
                ConnectorType = GetConnectorType()
            };
        }

        discoveryResult.ErrorMessage = formatErrorMessage(discoveryResult);
        discoveryResult.TelemetryProperties ??= new Dictionary<string, object>();

        discoveryResult.TelemetryProperties["ProjectTargetFramework"] = _targetFrameworkMoniker;
        discoveryResult.TelemetryProperties["ProjectReqnrollVersion"] = ReqnrollVersion;
        if (_projectSettings.IsSpecFlowProject)             
            discoveryResult.TelemetryProperties["LegacySpecFlow"] = true;
        discoveryResult.TelemetryProperties["ConnectorType"] = discoveryResult.ConnectorType;
        discoveryResult.TelemetryProperties["ConnectorArguments"] = result.Arguments;
        discoveryResult.TelemetryProperties["ConnectorExitCode"] = result.ExitCode;
        if (!string.IsNullOrEmpty(discoveryResult.ReqnrollVersion))
            discoveryResult.TelemetryProperties["ReqnrollVersion"] = discoveryResult.ReqnrollVersion;

        if (!string.IsNullOrEmpty(discoveryResult.ErrorMessage))
            discoveryResult.TelemetryProperties["Error"] = discoveryResult.ErrorMessage;

        // Discovery-result telemetry is not implemented in the LSP server yet; NullTelemetryService no-ops it.

        return discoveryResult;
    }

    private string GetDetailedErrorMessage(ProcessHelper.RunProcessResult result, string errorMessage, string command)
    {
        var exitCode = result.ExitCode < 0 ? "<not executed>" : result.ExitCode.ToString();
        return
            $"Error during {command}. {Environment.NewLine}Command executed:{Environment.NewLine}  {result.CommandLine}{Environment.NewLine}Exit code: {exitCode}{Environment.NewLine}Message: {Environment.NewLine}{errorMessage}";
    }

    /// <summary>Resolves the connector executable path (or a <c>dotnet exec</c> command line) that should be launched for binding discovery.</summary>
    protected abstract string GetConnectorPath(List<string> arguments);

    private static string ExtractJsonPayload(string stdout)
    {
        const string open  = ">>>>>>>>>>";
        const string close = "<<<<<<<<<<";
        var openIdx = stdout.IndexOf(open, StringComparison.Ordinal);
        if (openIdx < 0) return null;
        var afterOpen = stdout.IndexOf('\n', openIdx);
        if (afterOpen < 0) return null;
        var closeIdx = stdout.IndexOf(close, afterOpen, StringComparison.Ordinal);
        if (closeIdx < 0) return null;
        return stdout.Substring(afterOpen + 1, closeIdx - afterOpen - 1).Trim();
    }

    private string GetDotNetInstallLocation()
    {
        var programFiles = Environment.GetEnvironmentVariable("ProgramW6432");
        if (_processorArchitecture == ProcessorArchitectureSetting.X86)
            programFiles = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        if (string.IsNullOrEmpty(programFiles))
            programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
        return Path.Combine(programFiles!, "dotnet");
    }

    /// <summary>Appends <c>exec &lt;path&gt;</c> to <paramref name="arguments"/> and returns the path of the <c>dotnet</c> executable to invoke it with.</summary>
    protected string GetDotNetExecCommand(List<string> arguments, string executableFolder, string executableFile)
    {
#if DEBUG
        _logger.LogInfo($"Invoking '{executableFile}'...");
#endif
        arguments.Add("exec");
        arguments.Add(Path.Combine(executableFolder, executableFile));
        return GetDotNetCommand();
    }

    private string GetDotNetCommand()
    {
        if (!OperatingSystem.IsWindows())
            return ResolveNonWindowsDotNetCommand(
                Environment.GetEnvironmentVariable("DOTNET_ROOT"),
                Environment.GetEnvironmentVariable("HOME"),
                File.Exists);

        return Path.Combine(GetDotNetInstallLocation(), "dotnet.exe");
    }

    // No Windows-style Program Files layout on Linux/macOS. Prefer an explicit DOTNET_ROOT
    // (set by the .NET install scripts / CI images); otherwise try the conventional ~/.dotnet
    // install location the official dotnet-install.sh script uses — confirmed necessary live in
    // a Rider/Linux devcontainer: "dotnet" was not resolvable via PATH for our process (nor for
    // Rider's own JVM process, whose environment ours inherits), even though Rider's own Test
    // Explorer can build and run tests, meaning Rider locates dotnet some other way entirely
    // (its own SDK detection) that a plain child process doesn't get to share. Only fall back to
    // the bare command name (relying on PATH) as a last resort, for the cases where dotnet
    // genuinely is on PATH (e.g. CI images, or a host with a system-wide install).
    // Extracted as a pure function (taking the env var values and a File.Exists-shaped predicate
    // as parameters) so it can be unit-tested deterministically without depending on the host OS
    // or the real filesystem/environment.
    internal static string ResolveNonWindowsDotNetCommand(string dotNetRoot, string userHome, Func<string, bool> fileExists)
    {
        if (!string.IsNullOrEmpty(dotNetRoot))
            return Path.Combine(dotNetRoot, "dotnet");

        if (!string.IsNullOrEmpty(userHome))
        {
            var candidate = Path.Combine(userHome, ".dotnet", "dotnet");
            if (fileExists(candidate))
                return candidate;
        }

        return "dotnet";
    }

    /// <summary>Returns the extension's <c>Connectors</c> subfolder if it exists, otherwise falls back to the extension folder itself.</summary>
    protected string GetConnectorsFolder()
    {
        var connectorsFolder = Path.Combine(_extensionFolder, "Connectors");
        if (Directory.Exists(connectorsFolder))
            return connectorsFolder;
        return _extensionFolder;
    }
}
