using Reqnroll.Bindings.Provider.Data;

namespace ReqnrollConnector.Discovery;

public interface ISourceLocationProvider
{
    SourceLocation? GetSourceLocation(BindingSourceMethodData bindingMethod);
}
