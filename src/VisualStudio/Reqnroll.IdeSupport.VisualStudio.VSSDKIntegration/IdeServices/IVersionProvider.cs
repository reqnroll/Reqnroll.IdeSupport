using System;
using System.Linq;

namespace Reqnroll.IdeSupport.VisualStudio.IdeServices;

/// <summary>
/// Provides the running Visual Studio version and the extension's own version.
/// </summary>
public interface IVersionProvider
{
    /// <summary>Returns the version of the running Visual Studio instance.</summary>
    string GetVsVersion();

    /// <summary>Returns the version of this extension.</summary>
    string GetExtensionVersion();
}
