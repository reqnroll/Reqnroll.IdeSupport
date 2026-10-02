using Reqnroll.IdeSupport.Common.ProjectSystem.Settings;

namespace Reqnroll.IdeSupport.LSP.Server.Telemetry;

/// <summary>Helpers for the project-profile properties sent on server telemetry events (issue #846).</summary>
internal static class ProjectProfileTelemetry
{
    /// <summary>
    /// Maps a project file's extension to the <see cref="ProjectProgrammingLanguage"/> name
    /// (<c>CSharp</c>, <c>VB</c>, <c>FSharp</c> or <c>Other</c>). Only the enum name is returned,
    /// never any part of the path.
    /// </summary>
    internal static string GetProgrammingLanguage(string? projectFile)
    {
        var language = Path.GetExtension(projectFile ?? string.Empty).ToLowerInvariant() switch
        {
            ".csproj" => ProjectProgrammingLanguage.CSharp,
            ".vbproj" => ProjectProgrammingLanguage.VB,
            ".fsproj" => ProjectProgrammingLanguage.FSharp,
            _ => ProjectProgrammingLanguage.Other,
        };
        return language.ToString();
    }
}
