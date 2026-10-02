using Reqnroll.IdeSupport.Common.ProjectSystem;

namespace Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;

/// <summary>
/// Answers whether a project is a Reqnroll test project, gating the out-of-process connector so
/// it is never launched against an assembly whose bindings nothing would match (issue #731).
/// </summary>
/// <remarks>
/// Extracted as an interface so <see cref="ConnectorDiscoveryService"/> can be unit-tested with
/// the decision substituted; the production implementation is
/// <see cref="ReqnrollProjectDetector"/>.
/// </remarks>
public interface IReqnrollProjectDetector
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="scope"/> uses Reqnroll and owns at
    /// least one feature file, so discovery should run for it.
    /// </summary>
    bool IsReqnrollTestProject(IProjectScope scope);
}
