using Reqnroll.IdeSupport.Common.Configuration;
using System;

namespace Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;

/// <summary>IIdeSupportConfigurationProvider</summary>
public interface IIdeSupportConfigurationProvider
{
    /// <summary>Raised on any thread when configuration changes.</summary>
    /// <remarks>
    /// No production subscriber currently exists (issue #579). The one place a subscription was
    /// ever attempted, <c>ProjectSettingsProvider</c>'s <c>WeakConfigurationChanged</c> wiring, is
    /// commented out in both directions (subscribe and unsubscribe) and is itself legacy
    /// infrastructure never wired into the LSP-based architecture — the LSP server's
    /// <c>WatchedFilesHandler</c> calls <see cref="ProjectScopeIdeSupportConfigurationProvider.Reload"/>
    /// directly instead of relying on this event firing. Left in place rather than removed:
    /// whether the commented-out wiring represents unfinished work worth completing, or dead code
    /// left over from before the LSP rewrite, is a call for someone with more history on this
    /// interface than a static read can establish.
    /// </remarks>
    event EventHandler ConfigurationChanged;
    /// <summary>Returns the currently resolved configuration.</summary>
    IdeSupportConfiguration GetConfiguration();
}
