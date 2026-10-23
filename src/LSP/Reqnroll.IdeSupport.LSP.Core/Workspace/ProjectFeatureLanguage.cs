using Reqnroll.IdeSupport.Common.Configuration.Model;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;

namespace Reqnroll.IdeSupport.LSP.Core.Workspace;

/// <summary>
/// Resolves the Gherkin language a feature file is parsed with when it carries no
/// <c># language:</c> directive.
/// </summary>
public static class ProjectFeatureLanguage
{
    /// <summary>
    /// Returns the default feature language configured for <paramref name="project"/>
    /// (<c>language.feature</c> in <c>reqnroll.json</c>), or
    /// <see cref="IdeSupportConfiguration.FallbackFeatureLanguage"/> when there is no project.
    /// </summary>
    public static string For(IProjectScope? project) =>
        project?.GetIdeSupportConfiguration()?.DefaultFeatureLanguage is { Length: > 0 } language
            ? language
            : IdeSupportConfiguration.FallbackFeatureLanguage;
}
