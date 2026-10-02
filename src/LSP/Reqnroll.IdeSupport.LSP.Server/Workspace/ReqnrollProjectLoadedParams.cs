namespace Reqnroll.IdeSupport.LSP.Server.Workspace;

/// <summary>
/// Payload for the <c>reqnroll/projectLoaded</c> client-to-server notification.
/// Sent by each IDE glue component when a Reqnroll project is opened or its
/// build properties change (e.g. after a rebuild).
/// </summary>
/// <remarks>
/// Not a MediatR notification (issue #579) despite the name: this method is routed directly to
/// <see cref="ILspWorkspaceScopeManager.HandleProjectLoadedAsync"/> via
/// <c>OnNotification</c>/<c>LspHandlerResolver</c>, never through <c>IMediator.Publish</c>. A
/// previous <c>: INotification</c> marker here advertised a pub/sub extension point that did not
/// exist — a handler added for it would compile and register but never run.
/// </remarks>
public sealed class ReqnrollProjectLoadedParams
{
    /// <summary>
    /// Absolute path of the LSP workspace folder this project belongs to.
    /// Must match one of the paths sent in the LSP <c>initialize</c> handshake.
    /// </summary>
    public string WorkspaceFolder { get; set; } = string.Empty;

    /// <summary>Absolute path of the <c>.csproj</c> file.</summary>
    public string ProjectFile { get; set; } = string.Empty;

    /// <summary>Directory that contains the <c>.csproj</c> file.</summary>
    public string ProjectFolder { get; set; } = string.Empty;

    /// <summary>
    /// Absolute path of the primary output assembly, e.g.
    /// <c>C:\repos\MyApp\tests\MyApp.Tests\bin\Debug\net8.0\MyApp.Tests.dll</c>.
    /// May be empty when the project has not yet been built.
    /// </summary>
    public string OutputAssemblyPath { get; set; } = string.Empty;

    /// <summary>
    /// Full target-framework moniker as produced by MSBuild / the IDE project system,
    /// e.g. <c>.NETCoreApp,Version=v8.0</c> or <c>.NETFramework,Version=v4.8.1</c>.
    /// </summary>
    public string TargetFrameworkMoniker { get; set; } = string.Empty;

    /// <summary>
    /// The project's default namespace (e.g. <c>MyApp.Tests</c>).
    /// Used to derive namespaces for generated step-definition files.
    /// </summary>
    public string DefaultNamespace { get; set; } = string.Empty;

    /// <summary>
    /// NuGet package references resolved for this project.
    /// Used to identify Reqnroll projects and determine the Reqnroll version.
    /// </summary>
    public PackageReferenceInfo[] PackageReferences { get; set; } = [];
}

/// <summary>One resolved NuGet package reference.</summary>
public sealed class PackageReferenceInfo
{
    /// <summary>Gets or sets the package id.</summary>
    public string PackageId    { get; set; } = string.Empty;
    /// <summary>Gets or sets the version.</summary>
    public string Version      { get; set; } = string.Empty;
    /// <summary>
    /// Absolute path to the package's install directory in the NuGet cache,
    /// e.g. <c>C:\Users\me\.nuget\packages\reqnroll\2.1.0</c>.
    /// Empty when the install path is not available.
    /// </summary>
    public string InstallPath  { get; set; } = string.Empty;
}
