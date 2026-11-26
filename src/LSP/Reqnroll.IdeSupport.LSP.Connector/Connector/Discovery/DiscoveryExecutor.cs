using System.Diagnostics;
using Reqnroll.Bindings.Provider.Data;
using Reqnroll.IdeSupport.LSP.Connector.Models;
using ReqnrollConnector.AssemblyLoading;
using ReqnrollConnector.CommandLineOptions;
using ReqnrollConnector.Logging;
using ReqnrollConnector.SourceDiscovery;
using ReqnrollConnector.Utils;

namespace ReqnrollConnector.Discovery;

public class DiscoveryExecutor
{
    public static DiscoveryResult Execute(DiscoveryOptions options,
        ITestAssemblyContextFactory testAssemblyContextFactory, ILogger log, ITelemetryContainer telemetry)
    {
        log.Info($"Loading {options.AssemblyFile}");
        var testAssemblyContext = testAssemblyContextFactory.Create(options.AssemblyFile, log);
        telemetry.AddTelemetryProperty("ImageRuntimeVersion", testAssemblyContext.TestAssemblyImageRuntimeVersion);

        var targetFramework = testAssemblyContext.TestAssemblyTargetFrameworkName;
        if (targetFramework != null)
            telemetry.AddTelemetryProperty("TargetFramework", targetFramework);

        var reqnrollVersion = GetReqnrollVersion(testAssemblyContext.TestAssemblyLocation, log);
        if (reqnrollVersion != null)
        {
            telemetry.AddTelemetryProperty("SFFile", reqnrollVersion.InternalName ?? reqnrollVersion.FileName);
            telemetry.AddTelemetryProperty("SFFileVersion", reqnrollVersion.FileVersion ?? "Unknown");
            telemetry.AddTelemetryProperty("SFProductVersion", reqnrollVersion.ProductVersion ?? "Unknown");
        }

        string? configFileContent;
        try
        {
            configFileContent = LoadConfigFileContent(options.ConfigFile);
        }
        catch (Exception ex)
        {
            var msg = $"Could not load config file: {options.ConfigFile}";
            log.Error($"{msg}: {ex}");
            return CreateErrorResult(telemetry, msg, ex);
        }

        var bindingProvider = GetBindingProvider(targetFramework, reqnrollVersion, log);

        BindingData bindingData;
        try
        {
            bindingData = bindingProvider.DiscoverBindings(testAssemblyContext, configFileContent, log);
        }
        catch (Exception ex)
        {
            var msg = $"Could not discover bindings via: {bindingProvider}";
            log.Error($"{msg}: {ex}");
            return CreateErrorResult(telemetry, msg, ex);
        }

        InternalDiscoveryResult discoveryResult;
        try
        {
            var transformer = new DiscoveryResultTransformer();
            var sourceLocationProvider = new SourceLocationProvider(testAssemblyContext, log);
            discoveryResult = transformer.Transform(bindingData, sourceLocationProvider, telemetry, sourceLocationProvider);
        }
        catch (Exception ex)
        {
            var msg = "Could not transform discovery result.";
            log.Error($"{msg}: {ex}");
            return CreateErrorResult(telemetry, msg, ex);
        }

        return new DiscoveryResult
        {
            StepDefinitions = discoveryResult.StepDefinitions,
            Hooks = discoveryResult.Hooks,
            SourceFiles = new Dictionary<string, string>(discoveryResult.SourceFiles),
            TypeNames = new Dictionary<string, string>(discoveryResult.TypeNames),
            GenericBindingErrors = discoveryResult.GenericBindingErrors,
            LogMessages = discoveryResult.TypeLoadErrors.Select(e => $"Type or method has been skipped: {e}").ToArray(),
            TelemetryProperties = telemetry.ToDictionary()
        };
    }

    private static IBindingProvider GetBindingProvider(string? targetFramework, FileVersionInfo? reqnrollVersion, ILogger log)
    {
        // we could choose a version-specific binding provider here if needed
        var bindingProvider = new DefaultBindingProvider();
        log.Info($"Using binding provider: {bindingProvider} (target framework: {targetFramework}, Reqnroll version: {reqnrollVersion?.ProductVersion})");
        return bindingProvider;
    }

    private static string? LoadConfigFileContent(string? configFilePath)
    {
        if (string.IsNullOrEmpty(configFilePath))
            return null;

        var configFile = FileDetails.FromPath(configFilePath);
        if (configFile.Extension.Equals(".config", StringComparison.InvariantCultureIgnoreCase))
            return LegacyAppConfigLoader.LoadConfiguration(configFile);

        return File.ReadAllText(configFile.FullName);
    }

    private static FileVersionInfo? GetReqnrollVersion(string testAssemblyLocation, ILogger log)
    {
        var reqnrollAssemblyPath =
            Path.Combine(Path.GetDirectoryName(testAssemblyLocation) ?? ".", "Reqnroll.dll");
        if (File.Exists(reqnrollAssemblyPath))
            return GetReqnrollVersionInfo(reqnrollAssemblyPath, log);

        foreach (var otherReqnrollFile in Directory.EnumerateFiles(
                     Path.GetDirectoryName(reqnrollAssemblyPath)!, "Reqnroll*.dll"))
        {
            return GetReqnrollVersionInfo(otherReqnrollFile, log);
        }

        log.Info($"Not found {reqnrollAssemblyPath}");
        return null;
    }

    private static FileVersionInfo GetReqnrollVersionInfo(string reqnrollAssemblyPath, ILogger log)
    {
        var reqnrollVersion = FileVersionInfo.GetVersionInfo(reqnrollAssemblyPath);
        log.Info($"Found V{reqnrollVersion.FileVersion} at {reqnrollAssemblyPath}");
        return reqnrollVersion;
    }


    private static DiscoveryResult CreateErrorResult(ITelemetryContainer telemetry, string errorMessage, Exception? exception = null)
    {
        return new DiscoveryResult
        {
            TelemetryProperties = telemetry.ToDictionary(),
            ErrorMessage = exception != null ? $"{errorMessage}: {exception}" : errorMessage
        };
    }
}