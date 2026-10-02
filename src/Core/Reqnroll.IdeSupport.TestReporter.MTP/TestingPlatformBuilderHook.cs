using Microsoft.Testing.Platform.Builder;
using Microsoft.Testing.Platform.Extensions;

namespace Reqnroll.IdeSupport.TestReporter.MTP;

/// <summary>
/// Auto-registration entry point MTP's <c>Microsoft.Testing.Platform.MSBuild</c>-generated
/// <c>SelfRegisteredExtensions.AddSelfRegisteredExtensions</c> calls for every
/// <c>&lt;TestingPlatformBuilderHook&gt;</c> MSBuild item that names this class. Issue #741: that item
/// is added by <c>Reqnroll.IdeSupport.TestReporter.MTP.targets</c>, which also compiles this file (as
/// <c>internal</c>) into the user's own test assembly, so the generated call is an ordinary
/// same-assembly static call. Deliberately a plain static method, not an extension method.
/// </summary>
public static class TestingPlatformBuilderHook
{
    public static void AddExtensions(ITestApplicationBuilder testApplicationBuilder, string[] arguments)
    {
        var factory = new CompositeExtensionFactory<ReqnrollMtpReporter>(_ => new ReqnrollMtpReporter());
        // Microsoft.Testing.Platform 2.0.x only has AddTestSessionLifetimeHandle (no trailing "r");
        // 2.1.0 added AddTestSessionLifetimeHandler and made the old spelling [Obsolete(error: true)]
        // (CS0619, which no pragma can suppress). The injected sources must compile against the whole
        // supported [2.0.0, 3.0.0) range, so Reqnroll.IdeSupport.TestReporter.MTP.targets defines
        // REQNROLL_IDE_MTP_BEFORE_2_1 for a project whose resolved version is below 2.1.0 — and so does
        // this project's own csproj, which builds against the 2.0.0 floor.
#if REQNROLL_IDE_MTP_BEFORE_2_1
        testApplicationBuilder.TestHost.AddTestSessionLifetimeHandle(factory);
#else
        testApplicationBuilder.TestHost.AddTestSessionLifetimeHandler(factory);
#endif
        testApplicationBuilder.TestHost.AddDataConsumer(factory);
    }
}
