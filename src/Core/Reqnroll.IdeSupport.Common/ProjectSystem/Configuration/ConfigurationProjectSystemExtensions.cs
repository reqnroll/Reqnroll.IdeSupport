using Reqnroll.IdeSupport.Common.Configuration;

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

    /// <summary>Returns the resolved Deveroom configuration for the given IDE/project scope pair.</summary>
    public static IdeSupportConfiguration GetIdeSupportConfiguration(this IIdeScope ideScope, IProjectScope projectScope)
    {
        var provider = ideScope.GetIdeSupportConfigurationProvider(projectScope);
        return provider.GetConfiguration();
    }

    /// <summary>Returns the configuration provider for the given project scope, falling back to an IDE-scoped provider if <paramref name="projectScope"/> is <c>null</c>.</summary>
    public static IIdeSupportConfigurationProvider GetIdeSupportConfigurationProvider(this IIdeScope ideScope,
        IProjectScope projectScope)
    {
        if (projectScope != null)
            return projectScope.GetIdeSupportConfigurationProvider();
        return new ProjectSystemIdeSupportConfigurationProvider(ideScope);
    }
}
