using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.ProjectSystem;

namespace Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;

/// <summary>
/// Decides whether a project reported by the IDE glue is a Reqnroll (or legacy SpecFlow)
/// project at all, so that <see cref="ConnectorDiscoveryService"/> never spawns the
/// out-of-process connector against an assembly that cannot contain bindings (issue #731).
/// </summary>
/// <remarks>
/// <para>
/// None of the three clients filters what it sends: VS and VS Code report every project in the
/// solution/workspace and Rider every runnable project, so without this gate a solution with one
/// Reqnroll test project and twenty ordinary libraries paid for twenty-one connector processes —
/// each loading an unrelated assembly and its dependency closure into a runtime — on every build.
/// </para>
/// <para>
/// Two independent signals are accepted, and either one is enough:
/// </para>
/// <list type="number">
/// <item><description>
/// A NuGet package reference whose name mentions Reqnroll or SpecFlow. Deliberately a substring
/// match rather than the exact-name/version resolution <c>ReqnrollPackageDetector</c> performs:
/// this gate only has to answer "could this project possibly have bindings", and the extension
/// ecosystem (Reqnroll.MsTest, Reqnroll.SpecFlowCompatibility.ReqnrollPlugin,
/// SpecSync.AzureDevOps.Reqnroll.*, CucumberExpressions.SpecFlow.*, TechTalk.SpecFlow, …) is
/// open-ended. A project that references none of them is not a Reqnroll project.
/// </description></item>
/// <item><description>
/// A Reqnroll or SpecFlow runtime assembly sitting next to the project's output assembly. This
/// is what keeps Rider working: <c>ReqnrollProjectBaseline.kt</c> sends an empty
/// <c>packageReferences</c> list for every project because Rider exposes no model for resolved
/// NuGet references yet. It also covers VS sending an empty list transiently while NuGet is
/// still loading (issue #690), and projects that pull Reqnroll in transitively via an internal
/// meta-package rather than as a direct reference.
/// </description></item>
/// </list>
/// <para>
/// The assembly probe is only meaningful once the project has been built, which is exactly when
/// <see cref="ConnectorDiscoveryService"/> consults this type — it has already established that
/// the output assembly exists by then.
/// </para>
/// </remarks>
public sealed class ReqnrollProjectDetector : IReqnrollProjectDetector
{
    // Matched case-insensitively as substrings of the package name; see the class remarks for
    // why this is deliberately broader than ReqnrollPackageDetector's exact-name resolution.
    private static readonly string[] PackageNameMarkers = ["Reqnroll", "SpecFlow"];

    // Runtime assemblies that only ever appear in the output folder of a project that uses
    // Reqnroll or legacy SpecFlow. Same file names ReqnrollProjectSettingsProvider probes for
    // when it derives the version from the output folder.
    private static readonly string[] RuntimeAssemblyNames = ["Reqnroll.dll", "TechTalk.SpecFlow.dll"];

    private readonly IFileSystemForIDE _fileSystem;

    /// <summary>Initializes a new instance of the <see cref="ReqnrollProjectDetector"/> class.</summary>
    public ReqnrollProjectDetector(IFileSystemForIDE fileSystem)
    {
        _fileSystem = fileSystem;
    }

    /// <inheritdoc/>
    public bool IsReqnrollProject(IProjectScope scope)
    {
        if (HasReqnrollPackageReference(scope))
            return true;

        return HasReqnrollRuntimeAssemblyInOutputFolder(scope);
    }

    private static bool HasReqnrollPackageReference(IProjectScope scope) =>
        (scope.PackageReferences ?? []).Any(package =>
            package?.PackageName is { } name &&
            PackageNameMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase)));

    private bool HasReqnrollRuntimeAssemblyInOutputFolder(IProjectScope scope)
    {
        var outputAssemblyPath = scope.OutputAssemblyPath;
        if (string.IsNullOrWhiteSpace(outputAssemblyPath))
            return false;

        string? outputFolder;
        try
        {
            outputFolder = _fileSystem.Path.GetDirectoryName(outputAssemblyPath);
        }
        catch (ArgumentException)
        {
            // A malformed OutputAssemblyPath is the client's problem, not a reason to let an
            // unfiltered project through to the connector.
            return false;
        }

        if (string.IsNullOrEmpty(outputFolder))
            return false;

        return RuntimeAssemblyNames.Any(assemblyName =>
            _fileSystem.File.Exists(_fileSystem.Path.Combine(outputFolder, assemblyName)));
    }
}
