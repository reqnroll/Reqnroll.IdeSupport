#nullable enable

using Reqnroll.IdeSupport.LSP.Core.Bindings;

namespace Reqnroll.IdeSupport.LSP.Core.FindUnusedStepDefinitions;

/// <summary>Finds step definitions that have no matching step anywhere in the workspace's feature files.</summary>
public interface IFindUnusedStepDefinitionsService
{
    /// <summary>
    /// Scans every supplied project's binding registry and returns one <see cref="UnusedStepDefinition"/>
    /// per step-definition binding expression with zero matching steps across the workspace.
    /// </summary>
    IReadOnlyList<UnusedStepDefinition> FindUnusedStepDefinitions(
        IReadOnlyList<(string ProjectName, string ProjectFolder, ProjectBindingRegistry Registry)> registries);
}
