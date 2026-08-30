using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Configuration;
using System;

namespace Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;

/// <summary>ProjectSystemIdeSupportConfigurationProvider</summary>
public class ProjectSystemIdeSupportConfigurationProvider : IIdeSupportConfigurationProvider
{
    private readonly IdeSupportConfiguration _configuration;

    /// <summary>Initializes a new instance of the <see cref="ProjectSystemIdeSupportConfigurationProvider"/> class.</summary>
    public ProjectSystemIdeSupportConfigurationProvider(IIdeScope ideScope)
    {
        _configuration = new IdeSupportConfiguration(); //TODO: Load solution-level config
    }

    /// <summary>Raised on any thread when configuration changes. Never raised by this solution-level stub implementation.</summary>
    public event EventHandler ConfigurationChanged;

    /// <summary>Returns the solution-level configuration (currently a fresh default; solution-level loading is not yet implemented).</summary>
    public IdeSupportConfiguration GetConfiguration() => _configuration;
}
