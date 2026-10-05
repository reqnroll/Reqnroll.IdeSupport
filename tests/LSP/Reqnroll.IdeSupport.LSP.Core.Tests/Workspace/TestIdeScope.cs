using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Workspace;

/// <summary>
/// Minimal <see cref="IIdeScope"/> for the project-scope tests: a real file system and
/// substituted telemetry/actions, mirroring what the server's <c>LspIdeScope</c> provides.
/// </summary>
internal sealed class TestIdeScope : IIdeScope
{
    public TestIdeScope(IIdeSupportLogger logger)
    {
        Logger = logger;
        FileSystem = new FileSystemForIDE();
        TelemetryService = Substitute.For<ITelemetryService>();
        Actions = Substitute.For<IIdeActions>();
    }

    public bool IsSolutionLoaded => true;
    public IIdeSupportLogger Logger { get; }
    public ITelemetryService TelemetryService { get; }
    public IIdeActions Actions { get; }
    public IFileSystemForIDE FileSystem { get; }
}
