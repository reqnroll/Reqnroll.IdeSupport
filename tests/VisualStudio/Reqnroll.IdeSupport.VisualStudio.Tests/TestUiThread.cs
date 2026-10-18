using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.VisualStudio.Shell;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests;

/// <summary>
/// Runs a test body on a dedicated thread that <see cref="ThreadHelper"/> treats as the VS UI thread,
/// so code guarded by <see cref="ThreadHelper.ThrowIfNotOnUIThread"/> can be exercised outside a real
/// VS host. Outside VS, <see cref="ThreadHelper.CheckAccess"/> is always <see langword="false"/>
/// (it compares against a UI-thread dispatcher that only VS itself registers); this temporarily
/// registers the dedicated thread's dispatcher through ThreadHelper's own internal
/// <c>SetUIThread</c> hook and restores the previous registration afterwards.
/// </summary>
/// <remarks>
/// The registration is process-wide, so test classes using this must belong to
/// <see cref="TestUiThreadCollection"/>, which is not run in parallel with any other test.
/// </remarks>
internal static class TestUiThread
{
    private static readonly MethodInfo SetUiThreadMethod =
        typeof(ThreadHelper).GetMethod("SetUIThread", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ThreadHelper.SetUIThread not found.");

    private static readonly FieldInfo UiThreadDispatcherField =
        typeof(ThreadHelper).GetField("uiThreadDispatcher", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ThreadHelper.uiThreadDispatcher not found.");

    public static void Run(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var previousDispatcher = UiThreadDispatcherField.GetValue(null);
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                SetUiThreadMethod.Invoke(null, null);
                body();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                UiThreadDispatcherField.SetValue(null, previousDispatcher);
            }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}

/// <summary>Serialises tests that temporarily register a VS UI thread via <see cref="TestUiThread"/>.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TestUiThreadCollection
{
    public const string Name = "VS UI thread";
}
