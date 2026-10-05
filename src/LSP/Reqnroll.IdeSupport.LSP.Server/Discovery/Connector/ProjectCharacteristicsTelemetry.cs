using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;

/// <summary>
/// Builds the property set of the <c>ProjectCharacteristics</c> snapshot event (issues #258, #845):
/// size counts of a project's bindings and feature files, sent once per successful connector
/// discovery run. Counts only — no paths, class names or step text.
/// </summary>
/// <remarks>
/// Deliberately excluded (maintainer decision on #258): step-occurrence count (needs a full scan),
/// step-argument-transformation count (dropped by <c>RunDiscovery</c>) and any reuse ratio.
/// </remarks>
internal static class ProjectCharacteristicsTelemetry
{
    /// <summary>
    /// Builds the event properties. <paramref name="featureFileCount"/> and <paramref name="targetFramework"/>
    /// are omitted when null/empty (unknown), never sent as zero/blank; so are the test framework and
    /// platform inferred from <paramref name="packageReferences"/> when they cannot be determined.
    /// </summary>
    internal static Dictionary<string, object?> Build(
        ProjectBindingRegistry registry, int? featureFileCount, string? targetFramework,
        IEnumerable<NuGetPackageReference>? packageReferences = null)
    {
        var properties = new Dictionary<string, object?>
        {
            [TelemetryProperties.StepDefinitionCount] = registry.StepDefinitions.Length,
            [TelemetryProperties.HookCount] = registry.Hooks.Length,
            [TelemetryProperties.StepBindingClassCount] = CountBindingClasses(registry),
        };

        // The key suffix is a HookType name, so only defined members may reach it; anything else
        // (a future or corrupt value) is folded into Unknown rather than minting a new column.
        foreach (var group in registry.Hooks
                     .GroupBy(h => Enum.IsDefined(typeof(HookType), h.HookType) ? h.HookType : HookType.Unknown)
                     .OrderBy(g => g.Key))
            properties[TelemetryProperties.HookCountByTypePrefix + group.Key] = group.Count();

        if (featureFileCount is { } files)
            properties[TelemetryProperties.FeatureFileCount] = files;
        if (!string.IsNullOrEmpty(targetFramework))
            properties[TelemetryProperties.ProjectTargetFramework] = targetFramework;

        var testing = UnitTestFrameworkDetector.Detect(packageReferences);
        if (testing.Framework is not null)
            properties[TelemetryProperties.UnitTestFramework] = testing.Framework;
        if (testing.Platform is not null)
            properties[TelemetryProperties.TestPlatform] = testing.Platform;
        return properties;
    }

    /// <summary>
    /// Distinct declaring classes across step definitions and hooks, derived from the implementation's
    /// method identity. Two shapes exist: the connector's <c>{ShortTypeName}.{Signature}</c> (no namespace,
    /// with a parameter list, e.g. <c>Steps.SetFirstNumber(Int32)</c>) and Roslyn's
    /// <c>Namespace.Class.Method</c> (no parameters). Both are handled by cutting at the first <c>(</c>
    /// and then dropping the last <c>.</c>-segment (the method name). Identities without a <c>.</c>
    /// have no class part and are skipped. On the connector path classes are therefore namespace-less
    /// short names, so same-named classes in different namespaces merge: an accepted undercount.
    /// </summary>
    internal static int CountBindingClasses(ProjectBindingRegistry registry) =>
        registry.StepDefinitions.Select(s => s.Implementation?.Method)
            .Concat(registry.Hooks.Select(h => h.Implementation?.Method))
            .Select(DeclaringClass)
            .Where(c => c is not null)
            .Distinct(StringComparer.Ordinal)
            .Count();

    internal static string? DeclaringClass(string? method)
    {
        if (method is null)
            return null;
        // Cut the parameter list first: its types may themselves contain dots (System.String).
        var paren = method.IndexOf('(');
        var name = paren >= 0 ? method[..paren] : method;
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : null;
    }
}
