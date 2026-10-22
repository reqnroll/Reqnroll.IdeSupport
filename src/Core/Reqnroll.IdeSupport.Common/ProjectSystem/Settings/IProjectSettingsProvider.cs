#nullable enable


using System;

namespace Reqnroll.IdeSupport.Common.ProjectSystem.Settings;

/// <summary>IProjectSettingsProvider</summary>
public interface IProjectSettingsProvider
{
    /// <summary>Returns the currently cached project settings.</summary>
    ProjectSettings GetProjectSettings();
    /// <summary>Re-loads project settings from the project system and updates the cache if they changed.</summary>
    ProjectSettings CheckProjectSettings();
}
