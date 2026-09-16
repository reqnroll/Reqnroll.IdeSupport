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
using StreamJsonRpc;

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

    private static readonly SemaphoreSlim InitLock = new(1, 1);
    private static volatile bool _unavailable;
    private static object? _serviceProxy;
    private static MethodInfo? _getTestOutcomeMethod;

    /// <summary>
    /// Raised when VS's own test-outcome service reports that a test's cached result changed —
    /// i.e. a run completed (issue #700). Mirrors VS's own <c>AbstractTestProvider.TestChanged</c>
    /// static event (design doc §6/§7 item 3 follow-up), fired from <see cref="RunTestOutcomeCallbackTarget"/>
    /// once we've registered it as a local RPC target on the same connection <see cref="TryGetOutcomeAsync"/>
    /// already polls over (see <see cref="GetOrCreateProxyAsync"/>).
    /// </summary>
    /// <remarks>
    /// Payload is deliberately typed <see cref="object"/> rather than the real <c>TestMethodIdentifier</c>
    /// — that type lives in <c>Microsoft.VisualStudio.TestWindow.Internal.dll</c>, which this class's own
    /// tests (<c>RunTestOutcomeBridgeTests</c>) run in a plain xUnit host with no VS install/process, so
    /// it isn't on disk there. A static field's declared type is resolved when this class is first
    /// touched at all (any static member access), not lazily per-call, so putting the real type directly
    /// on this event would break every test in that file, not just ones exercising this event — subscribers
    /// pattern-match to <c>TestMethodIdentifier</c> themselves (see <see cref="RunTestCodeLensDataPoint"/>),
    /// which already requires that assembly and references it accordingly.
    /// </remarks>
    internal static event EventHandler<object>? TestChanged;

    /// <summary>
    /// Plain RPC target for VS's test-outcome service push notifications — deliberately <b>not</b> an
    /// implementation of the (internal, inaccessible) <c>ICodeLensTestInformationCallbackService</c>.
    /// <see cref="StreamJsonRpc.JsonRpc"/> dispatches incoming calls by matching public method
    /// name/signature on whatever target object it's given (confirmed via <c>JsonRpc.AddLocalRpcTarget</c>
    /// and <c>RemoteMethodNotFoundException</c> docs — plain reflection-based binding, no interface
    /// contract required), so this ordinary class satisfies the wire protocol without needing to
    /// implement anything internal. Method names/signatures must match
    /// <c>ICodeLensTestInformationCallbackService</c> exactly (decompiled from
    /// <c>Microsoft.VisualStudio.TestWindow.Internal.dll</c>) since that's what the server calls by name.
    /// </summary>
    /// <remarks>Internal rather than private so unit tests can exercise it directly without going through the real (unsupported) VS RPC connection.</remarks>
    internal sealed class RunTestOutcomeCallbackTarget
    {
        public Task OnTestChangedAsync(TestMethodIdentifier testMethod)
        {
            TestChanged?.Invoke(null, testMethod);
            return Task.CompletedTask;
        }

        public Task OnSettingsChangedAsync() => Task.CompletedTask;
    }

    /// <summary>
    /// Returns the cached outcome for <paramref name="testMethod"/>, or <c>null</c> when unknown, not
    /// yet run, or the underlying (unsupported, internal) API is unavailable for any reason —
    /// including a future VS update changing its shape. Never throws.
    /// </summary>
    /// <param name="dataPointId">
    /// Caller-owned subscription identity (issue #700 correction). The server's
    /// <c>SubscriptionTracker</c> (decompiled from <c>Microsoft.VisualStudio.TestWindow.Host.dll</c>)
    /// keys one test-method set per <paramref name="dataPointId"/> and <b>replaces</b> that set on
    /// every <c>GetTestOutcomeAsync</c> call — it does not merge. A single id shared across every
    /// scenario line (the original design) meant each line's poll silently evicted every other line's
    /// push subscription, so only whichever line polled most recently ever received a completion
    /// notification. Callers must pass a stable identity unique to what they're tracking — one per
    /// <see cref="RunTestCodeLensDataPoint"/> instance, mirroring VS's own <c>AbstractTestDataPoint.id</c>
    /// (an instance field, not shared), which this bridge originally deviated from.
    /// </param>
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
    public static async Task<RunTestOutcome?> TryGetOutcomeAsync(Guid dataPointId, TestMethodIdentifier testMethod, CancellationToken cancellationToken)
    {
        if (_unavailable)
            return null;

        try
        {
            var (proxy, getTestOutcomeMethod) = await GetOrCreateProxyAsync(cancellationToken).ConfigureAwait(false);
            if (proxy is null || getTestOutcomeMethod is null)
                return null;

            var outcomeName = await InvokeGetTestOutcomeAsync(proxy, getTestOutcomeMethod, dataPointId, testMethod, cancellationToken)
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
        object proxy, MethodInfo getTestOutcomeMethod, Guid dataPointId, object testMethod, CancellationToken cancellationToken)
    {
        var resultTask = (Task?)getTestOutcomeMethod.Invoke(proxy, new object[] { dataPointId, testMethod, cancellationToken });
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
            // Passing null here: ICodeLensTestInformationCallbackService is internal, so nothing in
            // our assembly can implement it to satisfy this constructor's parameter type. We attach
            // our own plain-object invalidation listener separately below, after construction, via
            // JsonRpc.AddLocalRpcTarget instead — StreamJsonRpc dispatches by public method
            // name/signature, not by interface, so that path needs no internal type at all.
            var proxy = ctor.Invoke(new object?[] { stream, null })
                ?? throw new InvalidOperationException("CodeLensTestInformationProxy construction returned null.");

            // Attach our own invalidation-push listener (issue #700) to the same duplex connection
            // TryGetOutcomeAsync polls over. CodeLensTestInformationProxy's own constructor already
            // called rpc.StartListening() before returning here, so AddLocalRpcTarget needs
            // AllowModificationWhileListening — StreamJsonRpc's documented, sanctioned way to add a
            // target after the fact (accepted race: a test-changed notification arriving in the brief
            // window before this call runs is missed for this process's lifetime of that one event;
            // self-heals on the next change). The private `rpc` field is declared as the fully public
            // StreamJsonRpc.JsonRpc, so everything past this reflective field read is ordinary,
            // compile-time-checked API — no further reflection needed for the callback wiring itself.
            var rpcField = proxyType.GetField("rpc", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingFieldException("CodeLensTestInformationProxy.rpc field not found.");
            var rpc = (JsonRpc)(rpcField.GetValue(proxy)
                ?? throw new InvalidOperationException("CodeLensTestInformationProxy.rpc was null."));
            rpc.AllowModificationWhileListening = true;
            rpc.AddLocalRpcTarget(new RunTestOutcomeCallbackTarget());

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
    /// <see cref="TypeLoadException"/>, <see cref="MissingMemberException"/> (covers the derived
    /// <see cref="MissingFieldException"/> too), or <see cref="InvalidCastException"/> means the API's
    /// shape has changed (permanent for the process's lifetime — see <see cref="DisablePermanently"/>);
    /// anything else is treated as transient (see <see cref="ResetForRetry"/>). Shared by every
    /// stage's catch clause so the two-way classification lives in exactly one place.
    /// </summary>
    /// <remarks>
    /// <see cref="InvalidCastException"/> is included because <see cref="GetOrCreateProxyAsync"/>'s
    /// reflective read of <c>CodeLensTestInformationProxy.rpc</c> casts the field's value to the public
    /// <c>StreamJsonRpc.JsonRpc</c> type — if a future VS update ever changes that field's declared
    /// type, the cast failing is exactly the same kind of permanent shape change a
    /// <see cref="MissingFieldException"/> represents, not a transient connection hiccup worth retrying
    /// on every subsequent poll.
    /// </remarks>
    internal static void HandleFailure(Exception ex, string step)
    {
        if (ex is TypeLoadException or MissingMemberException or InvalidCastException)
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
        TestChanged = null;
    }
}
