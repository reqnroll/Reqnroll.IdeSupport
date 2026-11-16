using System;
using System.IO;
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

    private static bool SameContent(string a, string b)
    {
        var infoA = new FileInfo(a);
        var infoB = new FileInfo(b);
        if (infoA.Length != infoB.Length)
            return false;

        using var streamA = File.OpenRead(a);
        using var streamB = File.OpenRead(b);
        var bufferA = new byte[8192];
        var bufferB = new byte[8192];
        int read;
        while ((read = streamA.Read(bufferA, 0, bufferA.Length)) > 0)
        {
            var offset = 0;
            while (offset < read)
            {
                var got = streamB.Read(bufferB, offset, read - offset);
                if (got == 0)
                    return false;
                offset += got;
            }

            for (var i = 0; i < read; i++)
                if (bufferA[i] != bufferB[i])
                    return false;
        }

        return true;
    }
}
