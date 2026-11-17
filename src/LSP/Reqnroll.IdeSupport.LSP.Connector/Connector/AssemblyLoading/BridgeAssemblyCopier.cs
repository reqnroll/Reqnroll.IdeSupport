using System;
using System.IO;
using System.Linq;
using ReqnrollConnector.Logging;

namespace ReqnrollConnector.AssemblyLoading;

/// <summary>
/// Places the AppDomain bridge assemblies next to a .NET Framework test assembly. A copy left by an
/// earlier build is replaced when its content differs from the connector's own, so the proxy interface
/// loaded in the test AppDomain is the same assembly the connector casts to.
/// </summary>
internal static class BridgeAssemblyCopier
{
    public static void EnsureCurrent(string source, string destination, ILogger log)
    {
        if (File.Exists(destination) && SameContent(source, destination))
            return;

        try
        {
            File.Copy(source, destination, overwrite: true);
        }
        catch (Exception ex)
        {
            log.Error($"Failed to copy bridge assembly '{source}' → '{destination}': {ex.Message}. " +
                      "The AppDomain proxy will likely fail to load.");
            throw;
        }
    }

    private static bool SameContent(string a, string b) =>
        new FileInfo(a).Length == new FileInfo(b).Length
        && File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b));
}
