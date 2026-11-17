using Reqnroll.IdeSupport.LSP.Core.Matching;

namespace Reqnroll.IdeSupport.LSP.Core.Workspace;

/// <summary>
/// A per-project counter of the project's configuration (for example the feature language set in
/// <c>reqnroll.json</c>). It goes up whenever a configuration change that can alter how the project's
/// feature files are parsed is detected.
/// </summary>
/// <remarks>
/// A configuration change re-derives every feature file of the project with the same text and, if
/// the assembly did not change, the same binding registry. This counter is what lets state derived
/// from those files record which configuration it was built with.
/// </remarks>
public interface IProjectConfigGenerations
{
    /// <summary>Returns the project's current configuration generation, or 0 if nothing has bumped it yet.</summary>
    long Current(ProjectOwner project);

    /// <summary>Advances the project's configuration generation by one and returns the new value.</summary>
    long Bump(ProjectOwner project);
}
