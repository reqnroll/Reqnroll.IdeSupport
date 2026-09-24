using Reqnroll.IdeSupport.Common.ProjectSystem;

namespace Reqnroll.IdeSupport.Common.Tests.ProjectSystem;

/// <summary>Covers reading a shared project's items through the importing project (issue #736).</summary>
public sealed class SharedProjectItemsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "reqnroll-736-" + Guid.NewGuid().ToString("N"));

    public SharedProjectItemsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void Returns_the_items_of_an_imported_projitems_as_full_paths()
    {
        var feature = Touch("Shared/Features/Calculator.feature");
        var steps = Touch("Shared/Steps/CalculatorSteps.cs");
        WriteProjItems("Shared/Shared.projitems",
            """<Compile Include="$(MSBuildThisFileDirectory)Steps\CalculatorSteps.cs" />""",
            """<None Include="$(MSBuildThisFileDirectory)Features\Calculator.feature" />""");
        var project = WriteProject("Tests/Tests.csproj", """<Import Project="..\Shared\Shared.projitems" Label="Shared" />""");

        SharedProjectItems.GetImportedFiles(project).Should().Equal(steps, feature);
    }

    [Fact]
    public void Returns_nothing_for_a_project_with_no_shared_import()
    {
        Touch("Tests/Steps.cs");
        var project = WriteProject("Tests/Tests.csproj", """<Import Project="Other.props" />""");
        Touch("Tests/Other.props");

        SharedProjectItems.GetImportedFiles(project).Should().BeEmpty();
    }

    [Fact]
    public void Returns_nothing_when_the_project_file_is_missing_or_malformed()
    {
        SharedProjectItems.GetImportedFiles(Path.Combine(_root, "Missing.csproj")).Should().BeEmpty();

        var malformed = Path.Combine(_root, "Malformed.csproj");
        File.WriteAllText(malformed, "<Project><Import");
        SharedProjectItems.GetImportedFiles(malformed).Should().BeEmpty();
    }

    [Fact]
    public void Skips_an_import_whose_projitems_does_not_exist()
    {
        var project = WriteProject("Tests/Tests.csproj", """<Import Project="..\Shared\Shared.projitems" />""");

        SharedProjectItems.GetImportedFiles(project).Should().BeEmpty();
    }

    [Fact]
    public void Resolves_an_import_written_with_MSBuildThisFileDirectory()
    {
        var steps = Touch("Shared/Steps.cs");
        WriteProjItems("Shared/Shared.projitems", """<Compile Include="$(MSBuildThisFileDirectory)Steps.cs" />""");
        var project = WriteProject("Tests/Tests.csproj",
            """<Import Project="$(MSBuildThisFileDirectory)..\Shared\Shared.projitems" />""");

        SharedProjectItems.GetImportedFiles(project).Should().Equal(steps);
    }

    [Fact]
    public void Skips_paths_naming_a_property_it_cannot_resolve()
    {
        Touch("Shared/Steps.cs");
        WriteProjItems("Shared/Shared.projitems", """<Compile Include="$(SomeOtherRoot)Steps.cs" />""");
        var project = WriteProject("Tests/Tests.csproj", """<Import Project="..\Shared\Shared.projitems" />""");

        SharedProjectItems.GetImportedFiles(project).Should().BeEmpty();
    }

    [Fact]
    public void Ignores_item_types_that_do_not_carry_sources_and_items_that_do_not_exist()
    {
        var steps = Touch("Shared/Steps.cs");
        Touch("Shared/Strings.resx");
        WriteProjItems("Shared/Shared.projitems",
            """<EmbeddedResource Include="$(MSBuildThisFileDirectory)Strings.resx" />""",
            """<Compile Include="$(MSBuildThisFileDirectory)Steps.cs" />""",
            """<Compile Include="$(MSBuildThisFileDirectory)Deleted.cs" />""");
        var project = WriteProject("Tests/Tests.csproj", """<Import Project="..\Shared\Shared.projitems" />""");

        SharedProjectItems.GetImportedFiles(project).Should().Equal(steps);
    }

    [Fact]
    public void Expands_wildcards_and_honours_Exclude_and_Remove()
    {
        var a = Touch("Shared/Features/A.feature");
        var b = Touch("Shared/Features/Nested/B.feature");
        Touch("Shared/Features/Draft.feature");
        Touch("Shared/Features/Nested/Removed.feature");
        WriteProjItems("Shared/Shared.projitems",
            """<None Include="$(MSBuildThisFileDirectory)**\*.feature" Exclude="$(MSBuildThisFileDirectory)Features\Draft.feature" />""",
            """<None Remove="$(MSBuildThisFileDirectory)Features\Nested\Removed.feature" />""");
        var project = WriteProject("Tests/Tests.csproj", """<Import Project="..\Shared\Shared.projitems" />""");

        SharedProjectItems.GetImportedFiles(project).Should().BeEquivalentTo(new[] { a, b });
    }

    [Fact]
    public void Merges_several_imports_without_duplicates()
    {
        var common = Touch("SharedA/Common.cs");
        var onlyB = Touch("SharedB/OnlyB.cs");
        WriteProjItems("SharedA/SharedA.projitems", """<Compile Include="$(MSBuildThisFileDirectory)Common.cs" />""");
        WriteProjItems("SharedB/SharedB.projitems",
            """<Compile Include="$(MSBuildThisFileDirectory)..\SharedA\Common.cs" />""",
            """<Compile Include="$(MSBuildThisFileDirectory)OnlyB.cs" />""");
        var project = WriteProject("Tests/Tests.csproj",
            """<Import Project="..\SharedA\SharedA.projitems" />""",
            """<Import Project="..\SharedB\SharedB.projitems" />""",
            """<Import Project="..\SharedA\SharedA.projitems" />""");

        SharedProjectItems.GetImportedFiles(project).Should().Equal(common, onlyB);
    }

    private string Touch(string relativePath)
    {
        var path = FullPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private string WriteProject(string relativePath, params string[] imports)
    {
        var path = FullPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            $"""
             <Project Sdk="Microsoft.NET.Sdk">
               <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
               {string.Join(Environment.NewLine, imports)}
             </Project>
             """);
        return path;
    }

    private void WriteProjItems(string relativePath, params string[] items)
    {
        var path = FullPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            $"""
             <?xml version="1.0" encoding="utf-8"?>
             <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
               <PropertyGroup>
                 <HasSharedItems>true</HasSharedItems>
               </PropertyGroup>
               <ItemGroup>
                 {string.Join(Environment.NewLine, items)}
               </ItemGroup>
             </Project>
             """);
    }

    private string FullPath(string relativePath)
        => Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
}
