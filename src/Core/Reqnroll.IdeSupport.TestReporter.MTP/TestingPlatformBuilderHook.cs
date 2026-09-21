using Microsoft.Testing.Platform.Builder;
using Microsoft.Testing.Platform.Extensions;

namespace Reqnroll.IdeSupport.TestReporter.MTP;

/// <summary>
/// Auto-registration entry point MTP's <c>Microsoft.Testing.Platform.MSBuild</c>-generated
/// <c>SelfRegisteredExtensions.AddSelfRegisteredExtensions</c> calls when this assembly is referenced
/// and a <c>&lt;TestingPlatformBuilderHook&gt;</c> MSBuild item names this class (issue #715 phase 2:
/// for now that item is declared directly in the test fixture project referencing this assembly for
/// testing purposes; ephemeral injection into a user's own project is phase 4).
/// </summary>
public static class TestingPlatformBuilderHook
{
    public static void AddExtensions(ITestApplicationBuilder testApplicationBuilder, string[] arguments)
    {
        var factory = new CompositeExtensionFactory<ReqnrollMtpReporter>(_ => new ReqnrollMtpReporter());
        testApplicationBuilder.TestHost.AddTestSessionLifetimeHandler(factory);
        testApplicationBuilder.TestHost.AddDataConsumer(factory);
    }
}
