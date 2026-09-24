using System.Diagnostics;

namespace Reqnroll.IdeSupport.TestReporter.MTP.Tests.Injection;

/// <summary>
/// A throwaway workspace (temp directory with a <c>.git</c> marker, so <c>WorkspaceRootLocator</c>
/// resolves it as the workspace root) holding one project, plus the issue #741 project-local stub
/// <c>obj/&lt;Project&gt;.csproj.reqnroll-ide.targets</c> that imports the source bundle this test
/// project's build output carries (<c>Reqnroll.IdeSupport.TestReporter.MTP.targets</c> +
/// <c>ReporterSource/*.cs</c>, flowed here as Content from the ProjectReference). Outside the repo on
/// purpose: the repo's Directory.Build.props (TreatWarningsAsErrors etc.) must not apply, and every
/// project is exactly what a user would have.
/// </summary>
internal sealed class InjectionWorkspace : IDisposable
{
    public const string StubSuffix = ".reqnroll-ide.targets";

    public static string BundleTargetsPath => Path.Combine(AppContext.BaseDirectory, "Reqnroll.IdeSupport.TestReporter.MTP.targets");

    public string Root { get; }
    public string ProjectDirectory { get; }
    public string ProjectFile { get; }

    private InjectionWorkspace(string root, string projectFileName)
    {
        Root = root;
        ProjectDirectory = Path.Combine(root, Path.GetFileNameWithoutExtension(projectFileName));
        ProjectFile = Path.Combine(ProjectDirectory, projectFileName);
    }

    public static InjectionWorkspace Create(string projectFileName, string projectXml, IReadOnlyDictionary<string, string>? files = null, bool writeStub = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "reqnroll-mtp-injection", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var workspace = new InjectionWorkspace(root, projectFileName);
        Directory.CreateDirectory(workspace.ProjectDirectory);
        File.WriteAllText(workspace.ProjectFile, projectXml);
        foreach (var (relativePath, content) in files ?? new Dictionary<string, string>())
        {
            var path = Path.Combine(workspace.ProjectDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        if (writeStub) workspace.WriteStub();
        return workspace;
    }

    /// <summary>Copies a directory's content into the project directory (used for the Reqnroll fixture's Features/StepDefinitions/global.json).</summary>
    public void CopyFrom(string sourceDirectory, params string[] relativePaths)
    {
        foreach (var relative in relativePaths)
        {
            var source = Path.Combine(sourceDirectory, relative);
            var destination = Path.Combine(ProjectDirectory, relative);
            if (File.Exists(source))
            {
                File.Copy(source, destination, overwrite: true);
                continue;
            }
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }
    }

    public string StubPath => Path.Combine(ProjectDirectory, "obj", Path.GetFileName(ProjectFile) + StubSuffix);

    /// <summary>Writes the stub in the same shape the IDEs do (MtpProjectStubs.cs/.kt/.ts): the escaped bundle path in a property, imported through it.</summary>
    public void WriteStub(string? importPath = null)
    {
        importPath ??= BundleTargetsPath;
        var escaped = importPath.Replace("%", "%25").Replace("$", "%24").Replace("@", "%40").Replace(";", "%3B")
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        Directory.CreateDirectory(Path.GetDirectoryName(StubPath)!);
        File.WriteAllText(StubPath,
            "<Project>\n" +
            $"  <PropertyGroup>\n    <_ReqnrollIdeMtpReporterBundle>{escaped}</_ReqnrollIdeMtpReporterBundle>\n  </PropertyGroup>\n" +
            "  <Import Project=\"$(_ReqnrollIdeMtpReporterBundle)\" Condition=\"Exists('$(_ReqnrollIdeMtpReporterBundle)')\" />\n" +
            "</Project>\n");
    }

    public string IntermediateDirectory(string tfm = "net10.0") => Path.Combine(ProjectDirectory, "obj", "Debug", tfm);
    public string OutputDirectory(string tfm = "net10.0") => Path.Combine(ProjectDirectory, "bin", "Debug", tfm);

    public string[] InjectedSources(string tfm = "net10.0")
    {
        var dir = Path.Combine(IntermediateDirectory(tfm), "ReqnrollIdeSupport");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.cs").Select(f => Path.GetFileName(f)).ToArray() : [];
    }

    public string SelfRegisteredExtensions(string tfm = "net10.0")
    {
        var candidates = Directory.Exists(IntermediateDirectory(tfm))
            ? Directory.GetFiles(IntermediateDirectory(tfm), "SelfRegisteredExtensions.*")
            : [];
        return candidates.Length == 0 ? string.Empty : File.ReadAllText(candidates[0]);
    }

    public bool ReporterHookRegistered(string tfm = "net10.0") =>
        SelfRegisteredExtensions(tfm).Contains("Reqnroll.IdeSupport.TestReporter.MTP.TestingPlatformBuilderHook.AddExtensions", StringComparison.Ordinal);

    public DotnetResult Dotnet(params string[] arguments) => Dotnet(null, arguments);

    public DotnetResult Dotnet(IReadOnlyDictionary<string, string>? environment, params string[] arguments)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = ProjectDirectory,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        // The outer test host is itself a dotnet/MSBuild/testing-platform process; none of its
        // environment may leak into the inner build or test run.
        foreach (var key in psi.Environment.Keys.Where(k =>
                     k.StartsWith("VSTEST_", StringComparison.OrdinalIgnoreCase) ||
                     k.StartsWith("TESTINGPLATFORM_", StringComparison.OrdinalIgnoreCase) ||
                     k.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase) ||
                     k.Equals("DOTNET_HOST_PATH", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(key);
        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
            psi.Environment[key] = value;

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)TimeSpan.FromMinutes(5).TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"dotnet {string.Join(' ', arguments)} did not finish within 5 minutes in {ProjectDirectory}");
        }
        return new DotnetResult(process.ExitCode, stdout.Result + stderr.Result);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* a lingering build-server handle; the OS temp cleanup will get it */ }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed record DotnetResult(int ExitCode, string Output);
