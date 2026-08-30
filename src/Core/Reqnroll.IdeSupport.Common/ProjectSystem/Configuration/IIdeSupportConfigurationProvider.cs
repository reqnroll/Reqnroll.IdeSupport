using Reqnroll.IdeSupport.Common.Configuration;
using System;

namespace Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;

/// <summary>IIdeSupportConfigurationProvider</summary>
public interface IIdeSupportConfigurationProvider
{
    /// <summary>Raised on any thread when configuration changes.</summary>
    event EventHandler ConfigurationChanged;
    /// <summary>Returns the currently resolved configuration.</summary>
    IdeSupportConfiguration GetConfiguration();
}
