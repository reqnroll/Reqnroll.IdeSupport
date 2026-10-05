using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Reflection;
using Reqnroll.IdeSupport.VisualStudio.Utilities;

namespace Reqnroll.IdeSupport.VisualStudio.IdeServices;

/// <summary>
/// Visual Studio's MEF-exported <see cref="IVersionProvider"/> implementation; lazily resolves
/// the VS product display version and this extension's own assembly version.
/// </summary>
[Export(typeof(IVersionProvider))]
public class VersionProvider : IVersionProvider
{
    private readonly Lazy<string> _lazyExtensionVersion;

    private readonly Lazy<string> _lazyVsVersion;
    private readonly IServiceProvider _serviceProvider;

    /// <summary>MEF importing constructor.</summary>
    [ImportingConstructor]
    public VersionProvider([Import(typeof(SVsServiceProvider))] IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _lazyVsVersion = new Lazy<string>(GetVsProductDisplayVersion);
        _lazyExtensionVersion = new Lazy<string>(ReadExtensionVersion);
    }

    /// <inheritdoc/>
    public string GetVsVersion() => _lazyVsVersion.Value;

    /// <inheritdoc/>
    public string GetExtensionVersion() => _lazyExtensionVersion.Value;

    private string GetVsProductDisplayVersion()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return VsUtils.GetVsProductDisplayVersionSafe(_serviceProvider);
    }

    private string ReadExtensionVersion()
    {
        var assembly = Assembly.GetAssembly(typeof(VersionProvider));
        var versionAttr = assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute))
            .OfType<AssemblyInformationalVersionAttribute>().FirstOrDefault();
        if (versionAttr != null) return versionAttr.InformationalVersion.Split('+', '-')[0];
        return assembly.GetName().Version.ToString(3);
    }
}
