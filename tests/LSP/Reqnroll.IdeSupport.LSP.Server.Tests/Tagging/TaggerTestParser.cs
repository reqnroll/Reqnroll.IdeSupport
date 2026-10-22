using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Tagging;

/// <summary>Creates the tag parser the way the server composes it.</summary>
internal static class TaggerTestParser
{
    public static IIdeSupportTagParser Create(IIdeSupportLogger logger) =>
        new IdeSupportTagParser(
            logger,
            Substitute.For<IErrorTelemetryService>(),
            new ProjectSystemIdeSupportConfigurationProvider(new LspIdeScope(logger)));
}
