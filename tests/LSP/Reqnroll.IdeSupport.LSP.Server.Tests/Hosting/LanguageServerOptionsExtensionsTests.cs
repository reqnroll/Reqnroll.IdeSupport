using System.Linq;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Server;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Hosting;

/// <summary>
/// Coverage for the manual protocol routing wired up by
/// <see cref="LanguageServerOptionsExtensions.InitializeCustomProtocolRouting"/>.
/// </summary>
public class LanguageServerOptionsExtensionsTests
{
    /// <summary>
    /// Project lifecycle notifications must run Serial, not the server's default Parallel for a
    /// manually-routed <c>OnNotification</c> with no explicit <see cref="JsonRpcHandlerOptions"/>.
    /// They all mutate the same <c>ILspWorkspaceScopeManager</c> scope table, and while that's a
    /// <c>ConcurrentDictionary</c> (safe against concurrent corruption), Parallel dispatch makes no
    /// guarantee about completion order — a fast <c>projectUnloaded</c> could finish before a
    /// slower, earlier-sent <c>projectLoaded</c> for the same project, leaving the scope loaded when
    /// the client already thinks it's gone (or the reverse, on a rapid solution reload).
    /// </summary>
    [Theory]
    [InlineData(LspMethodNames.ReqnrollProjectLoaded)]
    [InlineData(LspMethodNames.ReqnrollProjectUnloaded)]
    [InlineData(LspMethodNames.ReqnrollProjectFiles)]
    public void Project_lifecycle_notifications_are_registered_as_Serial(string method)
    {
        var options = new LanguageServerOptions();

        options.InitializeCustomProtocolRouting();

        var description = options.Handlers.Single(d => GetMethod(d) == method);
        description.Options.Should().NotBeNull();
        description.Options!.RequestProcessType.Should().Be(RequestProcessType.Serial);
    }

    /// <summary>
    /// Sanity check that not every manual route was swept into Serial by mistake — a read-only
    /// query like <c>reqnroll/resolveTestTargets</c> has no ordering requirement against other
    /// requests and should stay on the server's default Parallel lane.
    /// </summary>
    [Fact]
    public void ResolveTestTargets_is_not_registered_as_Serial()
    {
        var options = new LanguageServerOptions();

        options.InitializeCustomProtocolRouting();

        var description = options.Handlers.Single(d => GetMethod(d) == LspMethodNames.ReqnrollResolveTestTargets);
        description.Options?.RequestProcessType.Should().NotBe(RequestProcessType.Serial);
    }

    private static string? GetMethod(JsonRpcHandlerDescription description) => description switch
    {
        JsonRpcHandlerFactoryDescription factory => factory.Method,
        JsonRpcHandlerTypeDescription type => type.Method,
        JsonRpcHandlerInstanceDescription instance => instance.Method,
        _ => null,
    };
}
