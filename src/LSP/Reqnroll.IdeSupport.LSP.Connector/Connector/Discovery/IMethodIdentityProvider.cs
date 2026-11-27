using Reqnroll.Bindings.Provider.Data;
using Reqnroll.IdeSupport.LSP.Connector.Models;

namespace ReqnrollConnector.Discovery;

/// <summary>Determines the exact <see cref="MethodIdentity"/> of a discovered binding method.</summary>
public interface IMethodIdentityProvider
{
    /// <summary>
    /// Returns the identity of <paramref name="bindingMethod"/>, or <see langword="null"/> when it cannot be
    /// determined. Must not throw: the identity is an addition to discovery and never a reason for it to fail.
    /// </summary>
    MethodIdentity? GetMethodIdentity(BindingSourceMethodData bindingMethod);
}
