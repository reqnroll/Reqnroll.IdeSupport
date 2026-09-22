using Reqnroll.IdeSupport.Common.ProjectSystem;

namespace Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;

/// <summary>
/// Answers whether a project can contain Reqnroll bindings at all, gating the out-of-process
/// connector so it is never launched against an unrelated assembly (issue #731).
/// </summary>
/// <remarks>
/// Extracted as an interface so <see cref="ConnectorDiscoveryService"/> can be unit-tested with
/// the decision substituted; the production implementation is
/// <see cref="ReqnrollProjectDetector"/>.
/// </remarks>
public interface IReqnrollProjectDetector
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="scope"/> is a Reqnroll (or legacy
    /// SpecFlow) project and discovery should run for it.
    /// </summary>
    bool IsReqnrollProject(IProjectScope scope);
}
