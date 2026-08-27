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
    public static async Task<RunTestOutcome?> TryGetOutcomeAsync(TestMethodIdentifier testMethod, CancellationToken cancellationToken)
    {
        if (_unavailable)
            return null;

        try
        {
            var (proxy, getTestOutcomeMethod) = await GetOrCreateProxyAsync(cancellationToken).ConfigureAwait(false);
            if (proxy is null || getTestOutcomeMethod is null)
                return null;

            var resultTask = (Task?)getTestOutcomeMethod.Invoke(proxy, new object[] { DataPointId, testMethod, cancellationToken });
            if (resultTask is null)
                return null;

            await resultTask.ConfigureAwait(false);

            var resultProperty = resultTask.GetType().GetProperty("Result")
                ?? throw new MissingMemberException("GetTestOutcomeAsync's returned Task has no Result property.");
            var outcomeValue = resultProperty.GetValue(resultTask);
            return ParseOutcome(outcomeValue?.ToString());
        }
        catch (TypeLoadException ex) when (!cancellationToken.IsCancellationRequested)
        {
            DisablePermanently(ex, "TryGetOutcomeAsync");
            return null;
        }
        catch (MissingMemberException ex) when (!cancellationToken.IsCancellationRequested)
        {
            DisablePermanently(ex, "TryGetOutcomeAsync");
            return null;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Transient (this type's remarks) — e.g. a stale/dropped connection on an already-cached
            // proxy. Drop the cache so the next call reconnects from scratch instead of repeatedly
            // hitting the same dead proxy.
            ResetForRetry(ex, "TryGetOutcomeAsync");
            return null;
        }
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
        catch (TypeLoadException ex) when (!cancellationToken.IsCancellationRequested)
        {
            DisablePermanently(ex, "GetOrCreateProxyAsync");
            return (null, null);
        }
        catch (MissingMemberException ex) when (!cancellationToken.IsCancellationRequested)
        {
            DisablePermanently(ex, "GetOrCreateProxyAsync");
            return (null, null);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Transient (this type's remarks) — most commonly the outcome service not yet registered
            // right after a fresh VS launch. Not permanent: the next call (e.g. the next scenario line
            // CodeLens resolves, or a later refresh) gets a clean retry.
            ResetForRetry(ex, "GetOrCreateProxyAsync");
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

    private static RunTestOutcome? ParseOutcome(string? outcomeName) => outcomeName switch
    {
        "Passed" => RunTestOutcome.Passed,
        "Failed" => RunTestOutcome.Failed,
        "Skipped" => RunTestOutcome.Skipped,
        _ => null, // "None", "NotFound", an unrecognized future value, or null — all render as no glyph.
    };

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
}
