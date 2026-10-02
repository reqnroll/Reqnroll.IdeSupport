using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Tagging;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Hosting;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddReqnrollLspCoreServices_registers_the_requested_log_level_on_ClientIdeContext()
    {
        var provider = new ServiceCollection()
            .AddReqnrollLspCoreServices("vscode", TraceLevel.Verbose)
            .BuildServiceProvider();

        var context = provider.GetRequiredService<ClientIdeContext>();

        context.Ide.Should().Be("vscode");
        context.LogLevel.Should().Be(TraceLevel.Verbose);
    }

    [Fact]
    public void AddReqnrollLspCoreServices_defaults_the_log_level_to_Warning()
    {
        var provider = new ServiceCollection()
            .AddReqnrollLspCoreServices("visualstudio")
            .BuildServiceProvider();

        provider.GetRequiredService<ClientIdeContext>().LogLevel.Should().Be(TraceLevel.Warning);
    }

    [Fact]
    public void Define_steps_offer_tracker_resolves_and_is_wired_into_the_tagger_and_code_action_handler()
    {
        // The server facade is supplied by OmniSharp at runtime; a substitute stands in for it.
        var provider = new ServiceCollection()
            .AddReqnrollLspCoreServices("vscode")
            .AddReqnrollProjectSystem()
            .AddReqnrollEditorServices()
            .AddReqnrollLspHandlers()
            .AddSingleton(Substitute.For<OmniSharp.Extensions.LanguageServer.Protocol.Server.ILanguageServerFacade>())
            .AddSingleton(Substitute.For<MediatR.IMediator>())
            .BuildServiceProvider();

        var tracker = provider.GetRequiredService<IDefineStepsOfferTracker>();
        var tagger = provider.GetRequiredService<IGherkinDocumentTaggerService>();
        var handler = provider.GetRequiredService<Reqnroll.IdeSupport.LSP.Server.Features.CodeActions.CodeActionHandler>();

        tracker.Should().BeOfType<DefineStepsOfferTracker>();
        provider.GetRequiredService<IDefineStepsOfferTracker>().Should().BeSameAs(tracker, "it is a singleton shared by both emitters");

        // The tracker is an optional constructor parameter, so a missing registration would silently
        // degrade to "never emits"; assert the real instances received the registered singleton.
        PrivateField(tagger, "_offerTracker").Should().BeSameAs(tracker);
        PrivateField(handler, "_offerTracker").Should().BeSameAs(tracker);
    }

    private static object? PrivateField(object instance, string name) =>
        instance.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(instance);
}
