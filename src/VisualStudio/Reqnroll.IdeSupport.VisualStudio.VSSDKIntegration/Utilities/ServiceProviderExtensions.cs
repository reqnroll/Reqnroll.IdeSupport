#nullable disable

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Reqnroll.IdeSupport.VisualStudio.Utilities;

/// <summary>
/// Extension helpers for <see cref="IServiceProvider"/> and common VS SDK service interfaces.
/// </summary>
public static class ServiceProviderExtensions
{
    /// <summary>Gets the service of type <typeparamref name="T"/>, throwing if not found.</summary>
    public static T GetService<T>(this IServiceProvider serviceProvider) where T : class =>
        serviceProvider.GetService<T>(typeof(T));

    /// <summary>Gets the service registered under <paramref name="serviceType"/> cast to <typeparamref name="T"/>, throwing if not found.</summary>
    public static T GetService<T>(this IServiceProvider serviceProvider, Type serviceType) where T : class
    {
        if (serviceProvider == null) throw new ArgumentNullException(nameof(serviceProvider));
        if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
        T service = serviceProvider.TryGetService<T>(serviceType);
        if (service == null)
            throw new InvalidOperationException($"Service not found: {typeof(T)}");
        return service;
    }

    /// <summary>Gets the service registered under <paramref name="serviceType"/> cast to <typeparamref name="T"/>, or <see langword="null"/> if not found.</summary>
    public static T TryGetService<T>(this IServiceProvider serviceProvider, Type serviceType) where T : class
    {
        if (serviceProvider == null) throw new ArgumentNullException(nameof(serviceProvider));
        if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
        return serviceProvider.GetService(serviceType) as T;
    }

    /// <summary>Resolves the UI context cookie for the given command UI context <paramref name="id"/>.</summary>
    public static uint GetCmdUIContextCookie(this IVsMonitorSelection vsMonitorSelection, Guid id)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        ErrorHandler.ThrowOnFailure(vsMonitorSelection.GetCmdUIContextCookie(ref id, out var cookie));
        return cookie;
    }

    /// <summary>Returns the full path of the currently open solution file.</summary>
    public static string GetSolutionFile(this IVsSolution solution)
    {
        if (solution == null) throw new ArgumentNullException(nameof(solution));
        string empty1 = string.Empty;
        string empty2 = string.Empty;
        string empty3 = string.Empty;
        ThreadHelper.ThrowIfNotOnUIThread(nameof(GetSolutionFile));
        ErrorHandler.Succeeded(solution.GetSolutionInfo(out empty1, out empty2, out empty3));
        return empty2;
    }
}
