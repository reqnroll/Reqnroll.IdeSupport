using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;

namespace Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;

/// <summary>
/// Decides whether a project reported by the IDE glue is a Reqnroll project at all, so that
/// <see cref="ConnectorDiscoveryService"/> never spawns the out-of-process connector against an
/// assembly that cannot contain bindings (issue #731).
/// </summary>
/// <remarks>
/// <para>
/// None of the three clients filters what it sends: VS and VS Code report every project in the
/// solution/workspace and Rider every runnable project, so without this gate a solution with one
/// Reqnroll test project and twenty ordinary libraries paid for twenty-one connector processes —
/// each loading an unrelated assembly and its dependency closure into a runtime — on every build.
/// </para>
/// <para>
/// Three signals, in the order the legacy VS extension's
/// <c>ReqnrollProjectSettingsProvider</c> applied them:
/// </para>
/// <list type="number">
/// <item><description>
/// The <c>ide.reqnroll.isReqnrollProject</c> setting in the project's <c>reqnroll.json</c>, which
/// is authoritative in <b>both</b> directions when present: <see langword="true"/> forces
/// discovery on for a project neither heuristic below recognises, <see langword="false"/> forces
/// it off for one they would. This is the user's escape hatch from a wrong answer here, and
/// matches the tri-state <c>bool?</c> contract the legacy extension documented.
/// </description></item>
/// <item><description>
/// A NuGet package reference whose name mentions Reqnroll. Deliberately a substring match rather
/// than the exact-name/version resolution <c>ReqnrollPackageDetector</c> performs: this gate only
/// has to answer "could this project possibly have bindings", and one rule covers every current
/// test-framework package (Reqnroll.MsTest, Reqnroll.NUnit, Reqnroll.xUnit, Reqnroll.xunit.v3,
/// Reqnroll.TUnit), the tooling and plugin packages (Reqnroll.Tools.MsBuild.Generation,
/// Reqnroll.SpecFlowCompatibility.ReqnrollPlugin), third-party extensions
/// (SpecSync.AzureDevOps.Reqnroll.*) and any package added later, without this list going stale.
/// </description></item>
/// <item><description>
/// <c>Reqnroll.dll</c> sitting next to the project's output assembly. This is what keeps Rider
/// working: <c>ReqnrollProjectBaseline.kt</c> sends an empty <c>packageReferences</c> list for
/// every project because Rider exposes no model for resolved NuGet references yet. It also covers
/// VS sending an empty list transiently while NuGet is still loading (issue #690), and projects
/// that pull Reqnroll in transitively via an internal meta-package rather than as a direct
/// reference.
/// </description></item>
/// </list>
/// <para>
/// Legacy SpecFlow is deliberately not detected: it is out of scope for this tooling, so a
/// SpecFlow-only project is treated like any other non-Reqnroll project and skipped.
/// </para>
/// <para>
/// The assembly probe is only meaningful once the project has been built, which is exactly when
/// <see cref="ConnectorDiscoveryService"/> consults this type — it has already established that
/// the output assembly exists by then.
/// </para>
/// </remarks>
public sealed class ReqnrollProjectDetector : IReqnrollProjectDetector
{
    // Matched case-insensitively as a substring of the package name; see the class remarks for
    // why this is deliberately broader than ReqnrollPackageDetector's exact-name resolution.
    private const string PackageNameMarker = "Reqnroll";

    // The runtime assembly, which only ever lands in the output folder of a project that uses
    // Reqnroll. Same file name ReqnrollProjectSettingsProvider probes for when it derives the
    // version from the output folder.
    private const string RuntimeAssemblyName = "Reqnroll.dll";

    private readonly IFileSystemForIDE _fileSystem;

    /// <summary>Initializes a new instance of the <see cref="ReqnrollProjectDetector"/> class.</summary>
    public ReqnrollProjectDetector(IFileSystemForIDE fileSystem)
    {
        _fileSystem = fileSystem;
    }

    /// <inheritdoc/>
    public bool IsReqnrollProject(IProjectScope scope)
    {
        // Configured answer wins over both heuristics, on or off (see the class remarks). The
        // configuration is loaded once and cached in the project's property bag, and this same
        // call is made a few lines later by OutProcReqnrollConnectorFactory on the path this
        // gate guards, so consulting it here costs nothing extra.
        var configured = scope.GetIdeSupportConfiguration()?.Reqnroll?.IsReqnrollProject;
        if (configured.HasValue)
            return configured.Value;

        if (HasReqnrollPackageReference(scope))
            return true;

        return HasReqnrollRuntimeAssemblyInOutputFolder(scope);
    }

    private static bool HasReqnrollPackageReference(IProjectScope scope) =>
        (scope.PackageReferences ?? []).Any(package =>
            package?.PackageName is { } name &&
            name.Contains(PackageNameMarker, StringComparison.OrdinalIgnoreCase));

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

        return _fileSystem.File.Exists(_fileSystem.Path.Combine(outputFolder, RuntimeAssemblyName));
    }
}
