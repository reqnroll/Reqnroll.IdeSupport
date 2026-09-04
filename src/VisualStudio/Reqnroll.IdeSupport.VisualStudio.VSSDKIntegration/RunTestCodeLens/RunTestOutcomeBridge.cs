#nullable enable

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Core.Imaging;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.TestWindow;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;

/// <summary>Coarse pass/fail/skip result for the Run CodeLens glyph — deliberately not the real (also internal) <c>TestOutcome</c> enum, see <see cref="RunTestOutcomeBridge"/>.</summary>
internal enum RunTestOutcome
{
    Passed,
    Failed,
    Skipped,
}

/// <summary>
/// Best-effort bridge to VS's own cached test outcome, for the Run CodeLens pass/fail glyph (issue
/// #504 follow-up). This is the same mechanism VS's own built-in <c>TestStatusProvider</c> CodeLens
/// uses — decompiled from <c>Microsoft.VisualStudio.TestWindow.Internal.dll</c>/<c>...CodeLens.dll</c>
/// (design doc §6) — but <c>ICodeLensTestInformationService</c>, <c>CodeLensTestInformationProxy</c>,
/// and <c>RemoteTestWindowServiceProvider</c> are all <c>internal</c> to that assembly: not part of
/// the public extensibility surface, no compatibility guarantee, no deprecation notice if a VS
/// servicing update reshapes or removes any of it.
/// </summary>
/// <remarks>
/// Deliberately does <b>not</b> call <c>AbstractTestProvider.GetServiceProxyAsync</c> (the convenience
/// entry point VS's own <c>TestStatusProvider</c> uses) even though it's simpler — that method reads a
/// private static VS-process-id field that only gets populated once VS's own CSharp/Basic/C/C++-scoped
/// test CodeLens providers have themselves run at least once in this ServiceHub host. A user who only
/// ever opens `.feature` files might never trigger that. This class drives the same underlying
/// service independently instead.
///
/// <b>Every reflection step is wrapped</b> so a future VS update that renames, reshapes, or removes
/// any of this can only ever degrade the CodeLens back to "no glyph" (the pre-#504 behavior) — never
/// throw into the CodeLens host. Two different failure kinds are handled differently, deliberately:
/// a <see cref="TypeLoadException"/> or <see cref="MissingMemberException"/> (thrown by this class
/// itself when a type/field/method/constructor lookup comes back empty) means the API's *shape* has
/// changed — permanent for the process's lifetime, no point retrying. Any other exception (a
/// ServiceHub connection failure, the outcome service not yet registered right after a fresh VS
/// launch, a dropped RPC channel, ...) is treated as transient: logged, the cached connection is
/// dropped, and the next call tries again from scratch. The first cut of this class conflated the two
/// and permanently disabled itself on any failure at all — including a transient one hit only on a
/// fresh VS launch before the outcome service had finished registering, which made the glyph
/// (correctly working on a running VS) go permanently dark after every relaunch.
/// </remarks>
internal static class RunTestOutcomeBridge
{
    private static readonly IIdeSupportLogger Logger = new SynchronousFileLogger("vs", "ext", TraceLevel.Verbose);

    // Stable per-process id for the (never-unsubscribed) implicit "subscription" GetTestOutcomeAsync
    // establishes server-side — reused across every call so a long session accumulates at most one,
    // rather than one per scenario line ever computed.
    private static readonly Guid DataPointId = Guid.NewGuid();

    private static readonly SemaphoreSlim InitLock = new(1, 1);
    private static volatile bool _unavailable;
    private static object? _serviceProxy;
    private static MethodInfo? _getTestOutcomeMethod;

    /// <summary>
    /// Returns the cached outcome for <paramref name="testMethod"/>, or <c>null</c> when unknown, not
    /// yet run, or the underlying (unsupported, internal) API is unavailable for any reason —
    /// including a future VS update changing its shape. Never throws.
    /// </summary>
    /// <remarks>
    /// Three stages (issue #590), each independently testable: <b>acquire</b>
    /// (<see cref="GetOrCreateProxyAsync"/> — locate the assembly, resolve the internal types, bind
    /// the method handles; fails once, permanently), <b>invoke</b>
    /// (<see cref="InvokeGetTestOutcomeAsync"/> — call through the bound handle and await the
    /// returned <c>Task</c>), and <b>map</b> (<see cref="ParseOutcome"/> — translate the internal
    /// outcome value into <see cref="RunTestOutcome"/>). Each stage's failures are classified and
    /// dispatched by <see cref="HandleFailure"/>, tagged with the stage name that failed, so a
    /// future VS servicing update that reshapes this API is distinguishable in the log from "the
    /// test hasn't been run yet" — both of which surfaced identically as a silent <c>null</c> before
    /// this split.
    /// </remarks>
    public static async Task<RunTestOutcome?> TryGetOutcomeAsync(TestMethodIdentifier testMethod, CancellationToken cancellationToken)
    {
        if (_unavailable)
            return null;

        try
        {
            var (proxy, getTestOutcomeMethod) = await GetOrCreateProxyAsync(cancellationToken).ConfigureAwait(false);
            if (proxy is null || getTestOutcomeMethod is null)
                return null;

            var outcomeName = await InvokeGetTestOutcomeAsync(proxy, getTestOutcomeMethod, testMethod, cancellationToken)
                .ConfigureAwait(false);
            return ParseOutcome(outcomeName);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            HandleFailure(ex, "TryGetOutcomeAsync");
            return null;
        }
    }

    /// <summary>
    /// The <b>invoke</b> stage: calls the bound <c>GetTestOutcomeAsync</c> handle, awaits the
    /// returned (boxed, since the real return type is internal) <see cref="Task"/>, and reads its
    /// <c>Result</c> property via reflection. Returns <c>null</c> for a <c>null</c> result task —
    /// distinct from a resolved-but-unrecognized outcome name, which <see cref="ParseOutcome"/>
    /// (the map stage) handles instead. <paramref name="testMethod"/> is typed as <see cref="object"/>
    /// rather than <see cref="TestMethodIdentifier"/> deliberately: this stage only ever forwards it
    /// opaquely into the reflection-based <see cref="MethodInfo.Invoke"/> call below, so widening the
    /// parameter type lets this stage be exercised with a substituted handle in isolation, without
    /// pulling the (unsupported, internal, VS-install-version-pinned) test-window assembly into a
    /// unit test just to construct a fixture value that is never actually inspected here.
    /// </summary>
    internal static async Task<string?> InvokeGetTestOutcomeAsync(
        object proxy, MethodInfo getTestOutcomeMethod, object testMethod, CancellationToken cancellationToken)
    {
        var resultTask = (Task?)getTestOutcomeMethod.Invoke(proxy, new object[] { DataPointId, testMethod, cancellationToken });
        if (resultTask is null)
            return null;

        await resultTask.ConfigureAwait(false);

        var resultProperty = resultTask.GetType().GetProperty("Result")
            ?? throw new MissingMemberException("GetTestOutcomeAsync's returned Task has no Result property.");
        var outcomeValue = resultProperty.GetValue(resultTask);
        return outcomeValue?.ToString();
    }

    private static async Task<(object? Proxy, MethodInfo? GetTestOutcomeMethod)> GetOrCreateProxyAsync(CancellationToken cancellationToken)
    {
        if (_serviceProxy is not null && _getTestOutcomeMethod is not null)
            return (_serviceProxy, _getTestOutcomeMethod);

        await InitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_serviceProxy is not null && _getTestOutcomeMethod is not null)
                return (_serviceProxy, _getTestOutcomeMethod);
            if (_unavailable)
                return (null, null);

            // Internal.dll is already a real (public-type) reference of ours — TestMethodIdentifier
            // lives in it — so we can locate every internal sibling type via its own Assembly object,
            // no separate file lookup or Assembly.Load needed.
            var internalAssembly = typeof(TestMethodIdentifier).Assembly;

            var providerType = internalAssembly.GetType("Microsoft.VisualStudio.TestWindow.Extensibility.RemoteTestWindowServiceProvider")
                ?? throw new TypeLoadException("RemoteTestWindowServiceProvider type not found.");
            var instanceField = providerType.GetField("Instance", BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingFieldException("RemoteTestWindowServiceProvider.Instance field not found.");
            var providerInstance = instanceField.GetValue(null)
                ?? throw new InvalidOperationException("RemoteTestWindowServiceProvider.Instance was null.");

            var getStreamMethod = providerType.GetMethod("GetServiceStreamAsync", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new MissingMethodException("RemoteTestWindowServiceProvider.GetServiceStreamAsync method not found.");

            var serviceName = RuntimeInformation.OSArchitecture == Architecture.Arm64
                ? "CodeLensTestInformationService.arm64"
                : "CodeLensTestInformationService.x64";

            // The visualStudioProcessId parameter is decompiled as unused inside GetServiceStreamAsync's
            // own body (it never reaches HubClient.RequestServiceAsync) — passing 0 matches what the
            // shipped implementation actually does with it today. If a future VS build starts requiring
            // a real value, this whole call fails and is caught below like any other shape change.
            var streamTask = (Task?)getStreamMethod.Invoke(providerInstance, new object?[] { serviceName, 0, cancellationToken })
                ?? throw new InvalidOperationException("GetServiceStreamAsync returned no task.");
            await streamTask.ConfigureAwait(false);
            var stream = (Stream?)(streamTask.GetType().GetProperty("Result")
                    ?? throw new MissingMemberException("GetServiceStreamAsync's returned Task has no Result property."))
                .GetValue(streamTask)
                ?? throw new InvalidOperationException("GetServiceStreamAsync produced no stream.");

            var proxyType = internalAssembly.GetType("Microsoft.VisualStudio.TestWindow.CodeLens.CodeLensTestInformationProxy")
                ?? throw new TypeLoadException("CodeLensTestInformationProxy type not found.");
            var callbackInterfaceType = internalAssembly.GetType("Microsoft.VisualStudio.TestWindow.CodeLens.ICodeLensTestInformationCallbackService")
                ?? throw new TypeLoadException("ICodeLensTestInformationCallbackService type not found.");
            var ctor = proxyType.GetConstructor(
                    BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(Stream), callbackInterfaceType }, null)
                ?? throw new MissingMethodException("CodeLensTestInformationProxy(Stream, ICodeLensTestInformationCallbackService) constructor not found.");
            // Passing null for the callback target: we don't implement (or need) the invalidation
            // callback interface — this bridge only ever polls, it never subscribes to change
            // notifications. If the server ever calls back regardless, that's a no-op on our side.
            var proxy = ctor.Invoke(new object?[] { stream, null })
                ?? throw new InvalidOperationException("CodeLensTestInformationProxy construction returned null.");

            // GetTestOutcomeAsync is an explicit interface implementation on the concrete proxy type,
            // so it must be looked up via the (also internal) interface, not the concrete type — a
            // concrete-type GetMethod lookup would silently return null for an explicit implementation.
            var serviceInterfaceType = internalAssembly.GetType("Microsoft.VisualStudio.TestWindow.CodeLens.ICodeLensTestInformationService")
                ?? throw new TypeLoadException("ICodeLensTestInformationService type not found.");
            var getTestOutcomeMethod = serviceInterfaceType.GetMethod("GetTestOutcomeAsync")
                ?? throw new MissingMethodException("ICodeLensTestInformationService.GetTestOutcomeAsync method not found.");

            _serviceProxy = proxy;
            _getTestOutcomeMethod = getTestOutcomeMethod;
            return (proxy, getTestOutcomeMethod);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            HandleFailure(ex, "GetOrCreateProxyAsync");
            return (null, null);
        }
        finally
        {
            InitLock.Release();
        }
    }

    /// <summary>
    /// Maps a resolved outcome to a glyph, mirroring VS's own <c>TestStatusProvider.ToImageId</c>
    /// (decompiled, design doc §6) — unlike everything else in this class, <c>KnownMonikers</c> is a
    /// fully public, stable VS SDK API, so this part needs no reflection and no fallback.
    /// </summary>
    public static ImageId ToImageId(RunTestOutcome outcome)
    {
        var moniker = outcome switch
        {
            RunTestOutcome.Passed => KnownMonikers.StatusOK,
            RunTestOutcome.Failed => KnownMonikers.StatusError,
            RunTestOutcome.Skipped => KnownMonikers.StatusWarning,
            _ => KnownMonikers.StatusAlert,
        };
        return new ImageId(moniker.Guid, moniker.Id);
    }

    /// <summary>The <b>map</b> stage: translates the raw outcome name into <see cref="RunTestOutcome"/>. Never throws — an unrecognized or absent name renders as no glyph, the same as "not yet run".</summary>
    internal static RunTestOutcome? ParseOutcome(string? outcomeName) => outcomeName switch
    {
        "Passed" => RunTestOutcome.Passed,
        "Failed" => RunTestOutcome.Failed,
        "Skipped" => RunTestOutcome.Skipped,
        _ => null, // "None", "NotFound", an unrecognized future value, or null — all render as no glyph.
    };

    /// <summary>
    /// Classifies a caught exception and dispatches to the matching failure handler: a
    /// <see cref="TypeLoadException"/> or <see cref="MissingMemberException"/> means the API's shape
    /// has changed (permanent for the process's lifetime — see <see cref="DisablePermanently"/>);
    /// anything else is treated as transient (see <see cref="ResetForRetry"/>). Shared by every
    /// stage's catch clause so the two-way classification lives in exactly one place.
    /// </summary>
    internal static void HandleFailure(Exception ex, string step)
    {
        if (ex is TypeLoadException or MissingMemberException)
            DisablePermanently(ex, step);
        else
            ResetForRetry(ex, step);
    }

    /// <summary>Shape change (type/member no longer found) — permanent for the process's lifetime; retrying can't fix a reflection lookup that will keep failing the same way.</summary>
    private static void DisablePermanently(Exception ex, string step)
    {
        _unavailable = true;
        _serviceProxy = null;
        _getTestOutcomeMethod = null;
        Logger.LogWarning(
            $"RunTestOutcomeBridge: permanently disabling the Run CodeLens pass/fail glyph for this " +
            $"session — VS's internal test-outcome API ({step}) appears to have changed shape (type or " +
            $"member not found). This does not affect Run/Debug, which uses the public Test Explorer " +
            $"command surface instead.");
        Logger.LogException(ex, $"RunTestOutcomeBridge: {step} failed (shape change, permanent)");
    }

    /// <summary>Connection/runtime failure — logged and the cached connection dropped, but not permanent; the next call gets a clean retry (this type's remarks explain why the first cut got this wrong).</summary>
    private static void ResetForRetry(Exception ex, string step)
    {
        _serviceProxy = null;
        _getTestOutcomeMethod = null;
        Logger.LogException(ex, $"RunTestOutcomeBridge: {step} failed transiently — will retry on the next call");
    }

    /// <summary>Test-only: whether the bridge has latched itself off after a shape-change failure.</summary>
    internal static bool IsUnavailableForTests => _unavailable;

    /// <summary>Test-only: resets the cached proxy/handle and the permanent-disable latch to their initial state, since every field this class touches is static and would otherwise leak between tests.</summary>
    internal static void ResetStateForTests()
    {
        _unavailable = false;
        _serviceProxy = null;
        _getTestOutcomeMethod = null;
    }
}
