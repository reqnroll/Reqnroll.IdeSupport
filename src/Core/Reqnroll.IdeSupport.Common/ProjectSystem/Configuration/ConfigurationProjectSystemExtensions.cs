using Reqnroll.IdeSupport.Common.Configuration.Model;

namespace Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;

/// <summary>ConfigurationProjectSystemExtensions</summary>
public static class ConfigurationProjectSystemExtensions
{
    /// <summary>Returns the resolved Deveroom configuration for the given project scope.</summary>
    public static IdeSupportConfiguration GetIdeSupportConfiguration(this IProjectScope projectScope)
    {
        var provider = GetIdeSupportConfigurationProvider(projectScope);
        return provider.GetConfiguration();
    }

    /// <summary>Returns the configuration provider for the given project scope, creating and caching one if none exists yet.</summary>
    public static IIdeSupportConfigurationProvider GetIdeSupportConfigurationProvider(this IProjectScope projectScope)
    {
        return (IIdeSupportConfigurationProvider)projectScope.Properties.GetOrAdd(typeof(IIdeSupportConfigurationProvider), _ =>
            new ProjectScopeIdeSupportConfigurationProvider(projectScope));
    }
}
