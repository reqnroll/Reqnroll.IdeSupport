namespace Reqnroll.IdeSupport.Common.ProjectSystem.Settings;

/// <summary>Represents the discovered settings for a single project.</summary>
/// <param name="Kind">The project kind.</param>
/// <param name="TargetFrameworkMoniker">The target framework moniker.</param>
/// <param name="TargetFrameworkMonikers">The full target framework monikers string.</param>
/// <param name="PlatformTarget">The platform target.</param>
/// <param name="OutputAssemblyPath">The output assembly path.</param>
/// <param name="DefaultNamespace">The default namespace.</param>
/// <param name="ReqnrollVersion">The Reqnroll package version.</param>
/// <param name="ReqnrollGeneratorFolder">The Reqnroll generator folder path.</param>
/// <param name="ReqnrollConfigFilePath">The Reqnroll configuration file path.</param>
/// <param name="ReqnrollProjectTraits">The project traits flags.</param>
/// <param name="ProgrammingLanguage">The programming language.</param>
public record ProjectSettings(
    IdeSupportProjectKind Kind,
    TargetFrameworkMoniker TargetFrameworkMoniker,
    string TargetFrameworkMonikers,
    ProjectPlatformTarget PlatformTarget,
    string OutputAssemblyPath,
    string DefaultNamespace,
    NuGetVersion ReqnrollVersion,
    string ReqnrollGeneratorFolder,
    string ReqnrollConfigFilePath,
    ReqnrollProjectTraits ReqnrollProjectTraits,
    ProjectProgrammingLanguage ProgrammingLanguage
)
{
    /// <summary>Gets a value indicating whether the project is uninitialized.</summary>
    public bool IsUninitialized => Kind == IdeSupportProjectKind.Uninitialized;
    /// <summary>Gets a value indicating whether this is a Reqnroll test project.</summary>
    public bool IsReqnrollTestProject => Kind == IdeSupportProjectKind.ReqnrollTestProject;
    /// <summary>Gets a value indicating whether this is a Reqnroll library project.</summary>
    public bool IsReqnrollLibProject => Kind == IdeSupportProjectKind.ReqnrollLibProject;
    /// <summary>Gets a value indicating whether this is a Reqnroll project.</summary>
    public bool IsReqnrollProject => IsReqnrollTestProject || IsReqnrollLibProject;
    /// <summary>Gets a value indicating whether this is a SpecFlow project.</summary>
    public bool IsSpecFlowProject => IsReqnrollProject && ReqnrollProjectTraits.HasFlag(ReqnrollProjectTraits.LegacySpecFlow);

    /// <summary>Gets a value indicating whether design-time feature file generation is enabled.</summary>
    public bool DesignTimeFeatureFileGenerationEnabled =>
        ReqnrollProjectTraits.HasFlag(ReqnrollProjectTraits.DesignTimeFeatureFileGeneration);

    /// <summary>Gets a value indicating whether the project has a design-time generation replacement.</summary>
    public bool HasDesignTimeGenerationReplacement =>
        ReqnrollProjectTraits.HasFlag(ReqnrollProjectTraits.MsBuildGeneration) ||
        ReqnrollProjectTraits.HasFlag(ReqnrollProjectTraits.XUnitAdapter);

    /// <summary>Gets the Reqnroll version label.</summary>
    public string GetReqnrollVersionLabel() => ReqnrollVersion?.ToString() ?? "n/a";

    /// <summary>Gets a short display label for the project.</summary>
    public string GetShortLabel()
    {
        var result = $"{TargetFrameworkMoniker}";
        result += IsSpecFlowProject
            ? $",SpecFlow:{GetReqnrollVersionLabel()}"
            : $",Reqnroll:{GetReqnrollVersionLabel()}";
        if (PlatformTarget != ProjectPlatformTarget.Unknown && PlatformTarget != ProjectPlatformTarget.AnyCpu)
            result += "," + PlatformTarget;
        if (DesignTimeFeatureFileGenerationEnabled)
            result += ",Gen";
        return result;
    }
}