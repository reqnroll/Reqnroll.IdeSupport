using Reqnroll.IdeSupport.Common.Configuration.Model;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;

namespace Reqnroll.IdeSupport.LSP.Server.Workspace;

/// <summary>
/// The configuration for a document that no project owns: the built-in defaults. Each call returns a
/// fresh instance because <see cref="IdeSupportConfiguration"/> is mutable.
/// </summary>
internal sealed class NoProjectConfigurationProvider : IIdeSupportConfigurationProvider
{
    public static readonly NoProjectConfigurationProvider Instance = new();

    private NoProjectConfigurationProvider() { }

    public IdeSupportConfiguration GetConfiguration() => new();
}
