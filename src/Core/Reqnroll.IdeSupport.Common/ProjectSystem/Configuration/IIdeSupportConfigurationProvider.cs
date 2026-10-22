using Reqnroll.IdeSupport.Common.Configuration;

namespace Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;

/// <summary>IIdeSupportConfigurationProvider</summary>
public interface IIdeSupportConfigurationProvider
{
    /// <summary>Returns the currently resolved configuration.</summary>
    IdeSupportConfiguration GetConfiguration();
}
