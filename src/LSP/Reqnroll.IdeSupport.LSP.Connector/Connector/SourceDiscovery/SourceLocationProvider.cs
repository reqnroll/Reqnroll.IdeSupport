using System.Reflection;
using Reqnroll.Bindings.Provider.Data;
using Reqnroll.IdeSupport.LSP.Connector.Models;
using ReqnrollConnector.AssemblyLoading;
using ReqnrollConnector.Discovery;
using ReqnrollConnector.Logging;

namespace ReqnrollConnector.SourceDiscovery;

internal class SourceLocationProvider : ISourceLocationProvider, IMethodIdentityProvider
{
    private readonly SymbolReaderCache _symbolReaders;
    private readonly ITestAssemblyContext _assemblyLoadContext;
    private readonly ILogger _log;

    public SourceLocationProvider(ITestAssemblyContext assemblyLoadContext, ILogger log)
    {
        _symbolReaders = new SymbolReaderCache(log);
        _assemblyLoadContext = assemblyLoadContext;
        _log = log;
    }

    public MethodIdentity? GetMethodIdentity(BindingSourceMethodData bindingMethod)
    {
        if (bindingMethod.MetadataToken == null)
            return null;

        try
        {
            var reader = GetReader(bindingMethod);
            var identity = reader?.GetMethodIdentity(bindingMethod.MetadataToken.Value, bindingMethod.Type);
            if (identity == null)
                _log.Info($"No method identity for {bindingMethod.Type}::{bindingMethod.FullName}");
            return identity;
        }
        catch (Exception ex)
        {
            // Any failure here only costs the binding its identity; discovery itself must go on.
            _log.Info($"Cannot determine the method identity of {bindingMethod.Type}::{bindingMethod.FullName}: {ex.Message}");
            return null;
        }
    }

    private IdeSupportSymbolReader? GetReader(BindingSourceMethodData bindingMethod)
    {
        var assemblyNameStr = bindingMethod.Assembly ?? _assemblyLoadContext.TestAssemblyFullName!;
        var assemblyLocation = _assemblyLoadContext.AssemblyLocationFromAssemblyName(assemblyNameStr);
        return _symbolReaders[assemblyLocation];
    }

    public SourceLocation? GetSourceLocation(BindingSourceMethodData bindingMethod)
    {
        if (bindingMethod.MetadataToken == null)
            return null;

        var reader = GetReader(bindingMethod);

        if (reader == null)
            return null;

        var sequencePoints = reader.ReadMethodSymbol(bindingMethod.MetadataToken.Value);

        // Find start and end sequence points
        var (startSequencePoint, endSequencePoint) = sequencePoints.Aggregate(
            (startSequencePoint: (MethodSymbolSequencePoint?)null,
                endSequencePoint: (MethodSymbolSequencePoint?)null),
            (acc, cur) =>
            {
                if (acc.startSequencePoint == null)
                    return (cur, cur);
                return (acc.startSequencePoint, cur);
            }
        );

        // Use the first sequence point as a zero-width location (start == end).
        // PDB sequence points begin at the first executable statement, not the method
        // signature; using start==end keeps navigation consistent with the Roslyn path
        // (which anchors to the method identifier) — both produce no block-selection.
        if (startSequencePoint != null)
        {
            return new SourceLocation(
                startSequencePoint.SourcePath,
                startSequencePoint.StartLine,
                startSequencePoint.StartColumn,
                startSequencePoint.StartLine,
                startSequencePoint.StartColumn);
        }

        return null;
    }
}