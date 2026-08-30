namespace Reqnroll.IdeSupport.LSP.Core.Bindings;

/// <summary>
/// Stub <see cref="IBindingRegistryProvider"/> that always returns
/// <see cref="ProjectBindingRegistry.Invalid"/>. Used until a real
/// discovery connector is wired in.
/// </summary>
public sealed class NullBindingRegistryProvider : IBindingRegistryProvider
{
    /// <summary>Always returns <see cref="ProjectBindingRegistry.Invalid"/>, since no real discovery connector is wired in.</summary>
    public ProjectBindingRegistry Current => ProjectBindingRegistry.Invalid;

    /// <inheritdoc/>
    /// <remarks>Never raised by this implementation.</remarks>
    public event EventHandler<bool>? BindingRegistryChanged
    {
        add    { /* no-op */ }
        remove { /* no-op */ }
    }
}
