using Reqnroll.IdeSupport.LSP.Core.Bindings;

namespace Reqnroll.IdeSupport.LSP.Server.Telemetry;

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
    /// are omitted when null/empty (unknown), never sent as zero/blank.
    /// </summary>
    internal static Dictionary<string, object?> Build(
        ProjectBindingRegistry registry, int? featureFileCount, string? targetFramework)
    {
        var properties = new Dictionary<string, object?>
        {
            [TelemetryProperties.StepDefinitionCount] = registry.StepDefinitions.Length,
            [TelemetryProperties.HookCount] = registry.Hooks.Length,
            [TelemetryProperties.StepBindingClassCount] = CountBindingClasses(registry),
        };

        foreach (var group in registry.Hooks.GroupBy(h => h.HookType).OrderBy(g => g.Key))
            properties[TelemetryProperties.HookCountByTypePrefix + group.Key] = group.Count();

        if (featureFileCount is { } files)
            properties[TelemetryProperties.FeatureFileCount] = files;
        if (!string.IsNullOrEmpty(targetFramework))
            properties[TelemetryProperties.ProjectTargetFramework] = targetFramework;
        return properties;
    }

    /// <summary>
    /// Distinct declaring classes across step definitions and hooks: the implementation's method
    /// identity with its last <c>.</c>-segment (the method name) dropped. Identities without a
    /// <c>.</c> have no class part and are skipped.
    /// </summary>
    internal static int CountBindingClasses(ProjectBindingRegistry registry) =>
        registry.StepDefinitions.Select(s => s.Implementation?.Method)
            .Concat(registry.Hooks.Select(h => h.Implementation?.Method))
            .Select(DeclaringClass)
            .Where(c => c is not null)
            .Distinct(StringComparer.Ordinal)
            .Count();

    private static string? DeclaringClass(string? method)
    {
        if (method is null)
            return null;
        var dot = method.LastIndexOf('.');
        return dot > 0 ? method[..dot] : null;
    }
}
