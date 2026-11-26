using System.IO;
using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.GoToHooks;
using Reqnroll.IdeSupport.VisualStudio.Extension.Navigation;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.Navigation;

/// <summary>
/// The client side of navigating to a binding with no local source: reading the server's
/// <c>methodIdentity</c>, resolving it in the feature project's compilation
/// (<see cref="BindingSymbolResolver"/>), and building Go to Hooks targets for such hooks.
/// </summary>
public class ExternalBindingNavigationTests
{
    private const string BindingSource = """
        namespace First
        {
            public class Steps
            {
                public void Bind() { }
                public void Bind(int value) { }
            }
        }
        namespace Second
        {
            public class Steps
            {
                public void Bind(int value) { }
            }
        }
        """;

    private static readonly MetadataReference CoreLibrary = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);

    /// <summary>
    /// A "feature project" compilation that references a binding assembly, as the test project
    /// references a binding package: the binding methods are metadata symbols, not source.
    /// </summary>
    private static (Compilation FeatureProject, string BindingAssemblyName) ReferencingCompilation(string version = "1.2.3.4")
    {
        var bindingName = "Bindings" + Guid.NewGuid().ToString("N");
        var bindings = CSharpCompilation.Create(bindingName,
            new[] { CSharpSyntaxTree.ParseText($"[assembly: System.Reflection.AssemblyVersion(\"{version}\")]\n" + BindingSource) },
            new[] { CoreLibrary },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        bindings.Emit(stream).Success.Should().BeTrue();

        var featureProject = CSharpCompilation.Create("FeatureProject", references: new[]
        {
            CoreLibrary,
            MetadataReference.CreateFromImage(stream.ToArray()),
        });
        return (featureProject, bindingName);
    }

    private static BindingMethodIdentity Identity(string assemblyName, string id, string version = "1.2.3.4") =>
        new($"{assemblyName}, Version={version}, Culture=neutral, PublicKeyToken=null", id);

    [Theory]
    [InlineData("M:First.Steps.Bind", "First", 0)]
    [InlineData("M:First.Steps.Bind(System.Int32)", "First", 1)]
    [InlineData("M:Second.Steps.Bind(System.Int32)", "Second", 1)]
    public void Resolves_the_exact_referenced_method(string id, string expectedNamespace, int expectedParameters)
    {
        var (compilation, assemblyName) = ReferencingCompilation();

        var symbol = BindingSymbolResolver.Resolve(compilation, Identity(assemblyName, id));

        symbol.Should().NotBeNull();
        symbol!.ContainingNamespace.ToDisplayString().Should().Be(expectedNamespace);
        symbol.Parameters.Should().HaveCount(expectedParameters);
        symbol.Locations.Should().OnlyContain(l => l.IsInMetadata, "the binding comes from a referenced assembly");
    }

    [Theory]
    [InlineData("M:First.Steps.Bind(System.String)")]   // no such overload
    [InlineData("T:First.Steps")]                       // not a method
    [InlineData("M:Third.Steps.Bind")]                  // no such type
    public void Returns_null_for_an_id_that_names_no_method(string id)
    {
        var (compilation, assemblyName) = ReferencingCompilation();

        BindingSymbolResolver.Resolve(compilation, Identity(assemblyName, id)).Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_the_referenced_assembly_is_a_different_version()
    {
        // The project now references another build than the one discovery reflected over.
        var (compilation, assemblyName) = ReferencingCompilation(version: "2.0.0.0");

        BindingSymbolResolver.Resolve(compilation, Identity(assemblyName, "M:First.Steps.Bind", version: "1.2.3.4"))
            .Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_an_unparsable_assembly_identity()
    {
        var (compilation, _) = ReferencingCompilation();

        BindingSymbolResolver.Resolve(compilation, new BindingMethodIdentity("not, an=identity, =", "M:First.Steps.Bind"))
            .Should().BeNull();
    }

    [Fact]
    public void Method_identity_is_read_from_the_server_json()
    {
        var identity = BindingMethodIdentity.FromJson(new JObject
        {
            ["assemblyIdentity"] = "Bindings, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
            ["documentationCommentId"] = "M:First.Steps.Bind",
        });

        identity.Should().Be(new BindingMethodIdentity(
            "Bindings, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null", "M:First.Steps.Bind"));
    }

    [Fact]
    public void An_absent_or_incomplete_method_identity_reads_as_none()
    {
        BindingMethodIdentity.FromJson(null).Should().BeNull();
        BindingMethodIdentity.FromJson(JValue.CreateNull()).Should().BeNull();
        BindingMethodIdentity.FromJson(new JObject { ["assemblyIdentity"] = "Bindings" }).Should().BeNull();
    }

    [Fact]
    public void Unresolved_hooks_are_parsed_with_their_identity()
    {
        var result = FindHooksService.MapResult(new JObject
        {
            ["hooks"] = new JArray
            {
                new JObject
                {
                    ["uri"] = "", ["startLine"] = 0, ["startChar"] = 0, ["hookType"] = "BeforeScenario",
                    ["hookOrder"] = 10000, ["methodName"] = "Hooks.Setup()", ["isResolved"] = false,
                    ["methodIdentity"] = new JObject
                    {
                        ["assemblyIdentity"] = "Bindings, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
                        ["documentationCommentId"] = "M:First.Hooks.Setup",
                    },
                },
            },
        });

        var hook = result.Hooks.Should().ContainSingle().Subject;
        hook.IsResolved.Should().BeFalse();
        hook.MethodIdentity!.DocumentationCommentId.Should().Be("M:First.Hooks.Setup");
    }

    [Fact]
    public void A_hook_from_an_older_server_reads_as_resolved_without_identity()
    {
        var result = FindHooksService.MapResult(new JObject
        {
            ["hooks"] = new JArray
            {
                new JObject
                {
                    ["uri"] = "file:///c:/w/Hooks.cs", ["startLine"] = 3, ["startChar"] = 4,
                    ["hookType"] = "BeforeScenario", ["hookOrder"] = 10000, ["methodName"] = "Setup",
                },
            },
        });

        var hook = result.Hooks.Should().ContainSingle().Subject;
        hook.IsResolved.Should().BeTrue();
        hook.MethodIdentity.Should().BeNull();
    }

    [Fact]
    public void Hook_targets_open_local_hooks_by_file_and_unresolved_ones_by_identity()
    {
        var identity = new BindingMethodIdentity("Bindings, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null", "M:First.Hooks.Setup");
        var hooks = new[]
        {
            new HookLocation("file:///c:/w/Hooks.cs", 3, 4, "BeforeScenario", 10000, "Local"),
            new HookLocation("", 0, 0, "AfterScenario", 10000, "Hooks.Setup()", isResolved: false, methodIdentity: identity),
            new HookLocation("", 0, 0, "AfterScenario", 10000, "Hooks.Unknown()", isResolved: false),
        };

        var targets = HookLocationsMapper.BuildTargets(hooks);

        targets.Should().HaveCount(2, "an unresolved hook without an identity cannot be opened at all");
        targets[0].FilePath.Should().Be(@"c:\w\Hooks.cs");
        targets[0].Identity.Should().BeNull();
        targets[1].FilePath.Should().BeEmpty();
        targets[1].Identity.Should().Be(identity);
        targets[1].DisplayText.Should().Contain("Hooks.Setup()").And.Contain("referenced assembly");
    }
}
