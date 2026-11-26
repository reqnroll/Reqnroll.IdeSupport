using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using dnlib.DotNet;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ReqnrollConnector.SourceDiscovery;
using ReqnrollConnector.SourceDiscovery.DnLib;

namespace Reqnroll.IdeSupport.LSP.Connector.Tests.SourceDiscovery;

/// <summary>
/// Checks <see cref="DocumentationCommentIdBuilder"/> against Roslyn itself: every method of a
/// compiled sample is described from metadata and compared with the symbol's own
/// <c>GetDocumentationCommentId()</c>, so the IDs the connector emits are guaranteed to resolve
/// through <c>DocumentationCommentId.GetSymbolsForDeclarationId</c>.
/// </summary>
public class DocumentationCommentIdBuilderTests
{
    private const string Source = """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;

        public class TopLevelSteps
        {
            public void NoNamespace(int value) { }
        }

        namespace First
        {
            public enum Color { Red }
            public class Book { public class Chapter { } }

            public class Steps
            {
                public void Parameterless() { }
                public void Overloaded(int value) { }
                public void Overloaded(string value) { }
                public void Overloaded(int value, string text) { }
                public static void Static(decimal amount) { }
                public Task Async(DateTime when) => Task.CompletedTask;
                public void Nullable(int? count, Guid? id, Color? color) { }
                public void Collections(List<Book> books, IEnumerable<string> names, Dictionary<string, List<int>> map) { }
                public void Tuple((int, string) pair) { }
                public void Arrays(int[] vector, string[][] jagged, int[,] matrix, Book[,,] cube) { }
                public void Nested(Book.Chapter chapter, List<Book.Chapter> chapters) { }
                public void Dynamic(dynamic anything, object plain) { }
                public void NestedFromAnotherAssembly(Environment.SpecialFolder folder) { }
                public void NativeInts(nint a, nuint b) { }
                public void ByRef(ref int a, out string b, in long c) { b = ""; }
                public unsafe void Pointer(int* p) { }
                public void Params(params string[] values) { }
                public void Ünicode(int value) { }

                public class NestedSteps
                {
                    public void InNested(Color color) { }
                }
            }
        }

        namespace Second.Deeper
        {
            public class Steps
            {
                public void Overloaded(int value) { }
            }
        }
        """;

    private static (CSharpCompilation Compilation, byte[] Image) Compile()
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("IdentitySample" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(Source) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        result.Success.Should().BeTrue(string.Join(Environment.NewLine, result.Diagnostics));
        return (compilation, stream.ToArray());
    }

    private static IEnumerable<IMethodSymbol> OrdinaryMethods(INamespaceOrTypeSymbol container)
    {
        foreach (var member in container.GetMembers())
        {
            switch (member)
            {
                case INamespaceOrTypeSymbol nested:
                    foreach (var method in OrdinaryMethods(nested))
                        yield return method;
                    break;
                case IMethodSymbol { MethodKind: MethodKind.Ordinary } method:
                    yield return method;
                    break;
            }
        }
    }

    [Fact]
    public void Build_matches_Roslyn_for_every_ordinary_method_of_the_sample()
    {
        var (compilation, image) = Compile();
        var module = ModuleDefMD.Load(image);
        var roslynIds = OrdinaryMethods(compilation.Assembly.GlobalNamespace)
            .Select(m => m.GetDocumentationCommentId())
            .ToHashSet();

        var builtIds = module.GetTypes()
            .SelectMany(t => t.Methods)
            .Where(m => !m.IsSpecialName && !m.IsConstructor)
            .Select(DocumentationCommentIdBuilder.Build)
            .ToList();

        builtIds.Should().NotContainNulls();
        builtIds.Should().BeEquivalentTo(roslynIds);
        roslynIds.Should().HaveCountGreaterThanOrEqualTo(20, "the sample has to exercise the signature shapes it lists");
    }

    [Fact]
    public void Built_ids_resolve_back_to_exactly_one_symbol()
    {
        var (compilation, image) = Compile();
        var module = ModuleDefMD.Load(image);

        foreach (var method in module.GetTypes().SelectMany(t => t.Methods).Where(m => !m.IsSpecialName && !m.IsConstructor))
        {
            var id = DocumentationCommentIdBuilder.Build(method)!;
            DocumentationCommentId.GetSymbolsForDeclarationId(id, compilation).Should().ContainSingle(id);
        }
    }

    [Theory]
    [InlineData("public class C { public void M<T>(T value) { } }", "M")]
    [InlineData("public class C<T> { public void M(int value) { } }", "M")]
    [InlineData("public class O<T> { public class C { public void M(int value) { } } }", "M")]
    [InlineData("public class C { public int P { get; set; } }", "get_P")]
    [InlineData("public class C { public static C operator +(C a, C b) => a; }", "op_Addition")]
    public void Build_returns_null_for_methods_Reqnroll_never_binds(string source, string methodName)
    {
        var compilation = CSharpCompilation.Create("Unsupported" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(source) },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        compilation.Emit(stream).Success.Should().BeTrue();
        var module = ModuleDefMD.Load(stream.ToArray());

        var method = module.GetTypes().SelectMany(t => t.Methods).Single(m => m.Name == methodName);

        DocumentationCommentIdBuilder.Build(method).Should().BeNull();
    }

    [Fact]
    public void Build_returns_null_for_a_missing_method() =>
        DocumentationCommentIdBuilder.Build(null).Should().BeNull();

    [Fact]
    public void Reader_identity_carries_the_reflection_assembly_name_and_checks_the_declaring_type()
    {
        var (_, image) = Compile();
        var context = new AssemblyLoadContext(nameof(Reader_identity_carries_the_reflection_assembly_name_and_checks_the_declaring_type), isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(image));
            var method = assembly.GetType("First.Steps")!.GetMethod("Parameterless")!;
            var reader = new DnLibIdeSupportSymbolReader(ModuleDefMD.Load(image));

            var identity = reader.GetMethodIdentity(method.MetadataToken, "First.Steps");

            identity.Should().NotBeNull();
            identity!.AssemblyIdentity.Should().Be(assembly.FullName);
            identity.DocumentationCommentId.Should().Be("M:First.Steps.Parameterless");

            reader.GetMethodIdentity(method.MetadataToken, "Second.Deeper.Steps")
                .Should().BeNull("a token resolved against the wrong declaring type must not produce an identity");
            reader.GetMethodIdentity(assembly.GetType("First.Steps")!.MetadataToken, null)
                .Should().BeNull("a type token is not a method token");

            var nested = assembly.GetType("First.Steps+NestedSteps")!.GetMethod("InNested")!;
            reader.GetMethodIdentity(nested.MetadataToken, "First.Steps+NestedSteps")!.DocumentationCommentId
                .Should().Be("M:First.Steps.NestedSteps.InNested(First.Color)");
        }
        finally
        {
            context.Unload();
        }
    }
}
