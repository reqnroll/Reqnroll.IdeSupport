using Reqnroll.Bindings.Provider.Data;

namespace ReqnrollConnector.Discovery;

// ReSharper disable once UnusedMember.Global
public class NullSourceLocationProvider : ISourceLocationProvider
{
    public SourceLocation? GetSourceLocation(BindingSourceMethodData bindingMethod) => null;
}
