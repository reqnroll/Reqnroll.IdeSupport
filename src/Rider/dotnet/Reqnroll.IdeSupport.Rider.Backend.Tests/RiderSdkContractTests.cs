using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Reqnroll.IdeSupport.Rider.Backend.UnitTesting;
using Xunit;

namespace Reqnroll.IdeSupport.Rider.Backend.Tests;

/// <summary>
/// Checks the internal Rider type <see cref="UnitTestHighlightingContract"/> describes against the Rider SDK the backend
/// module is built with. The module creates it by reflection, so without this a <c>riderSdkVersion</c> bump that
/// renames or reshapes it would only show at run time, as .feature files without test gutter marks.
/// </summary>
public class RiderSdkContractTests
{
    [Fact]
    public void The_sdk_declares_the_highlighting_type()
    {
        using var assembly = SdkAssembly.Open();

        Assert.True(assembly.FindType(UnitTestHighlightingContract.TypeName).HasValue,
            $"{UnitTestHighlightingContract.TypeName} is not in {assembly.Path}.");
    }

    [Fact]
    public void The_highlighting_type_has_the_constructor_the_gutter_stage_calls()
    {
        using var assembly = SdkAssembly.Open();
        var type = assembly.FindType(UnitTestHighlightingContract.TypeName)
                   ?? throw new InvalidOperationException($"{UnitTestHighlightingContract.TypeName} is not in {assembly.Path}.");

        var constructors = assembly.ConstructorParameterTypes(type);

        Assert.True(
            constructors.Any(parameters => parameters.SequenceEqual(UnitTestHighlightingContract.ConstructorParameterTypeNames)),
            "Expected a constructor (" + string.Join(", ", UnitTestHighlightingContract.ConstructorParameterTypeNames) +
            "); found: " + string.Join(" | ", constructors.Select(parameters => "(" + string.Join(", ", parameters) + ")")));
    }

    [Fact]
    public void The_highlighting_type_is_a_daemon_highlighting()
    {
        using var assembly = SdkAssembly.Open();
        var type = assembly.FindType(UnitTestHighlightingContract.TypeName)
                   ?? throw new InvalidOperationException($"{UnitTestHighlightingContract.TypeName} is not in {assembly.Path}.");

        Assert.Contains(UnitTestHighlightingContract.HighlightingInterfaceName, assembly.InterfaceNames(type));
    }

    /// <summary>Metadata of the SDK's JetBrains.ReSharper.UnitTestFramework.dll, whose path the build records.</summary>
    private sealed class SdkAssembly : IDisposable
    {
        private readonly FileStream _stream;
        private readonly PEReader _pe;
        private readonly MetadataReader _metadata;

        private SdkAssembly(string path)
        {
            Path = path;
            _stream = File.OpenRead(path);
            _pe = new PEReader(_stream);
            _metadata = _pe.GetMetadataReader();
        }

        public string Path { get; }

        public static SdkAssembly Open()
        {
            var path = typeof(RiderSdkContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .SingleOrDefault(attribute => attribute.Key == "RiderSdkUnitTestFramework")?.Value;
            Assert.False(string.IsNullOrEmpty(path), "The build recorded no RiderSdkUnitTestFramework path.");
            Assert.True(File.Exists(path), $"The recorded Rider SDK assembly does not exist: {path}");
            Assert.Equal(UnitTestHighlightingContract.AssemblyName, System.IO.Path.GetFileNameWithoutExtension(path));
            return new SdkAssembly(path!);
        }

        public TypeDefinitionHandle? FindType(string fullName)
        {
            foreach (var handle in _metadata.TypeDefinitions)
            {
                var type = _metadata.GetTypeDefinition(handle);
                if (Name(type.Namespace, type.Name) == fullName)
                    return handle;
            }

            return null;
        }

        public List<string[]> ConstructorParameterTypes(TypeDefinitionHandle type)
        {
            var provider = new TypeNameProvider();
            return _metadata.GetTypeDefinition(type).GetMethods()
                .Select(_metadata.GetMethodDefinition)
                .Where(method => _metadata.GetString(method.Name) == ".ctor")
                .Select(method => method.DecodeSignature(provider, null).ParameterTypes.ToArray())
                .ToList();
        }

        public List<string> InterfaceNames(TypeDefinitionHandle type) =>
            _metadata.GetTypeDefinition(type).GetInterfaceImplementations()
                .Select(handle => TypeName(_metadata, _metadata.GetInterfaceImplementation(handle).Interface))
                .ToList();

        public void Dispose()
        {
            _pe.Dispose();
            _stream.Dispose();
        }

        private string Name(StringHandle ns, StringHandle name) =>
            _metadata.GetString(ns).Length == 0 ? _metadata.GetString(name) : _metadata.GetString(ns) + "." + _metadata.GetString(name);

        private static string TypeName(MetadataReader metadata, EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.TypeDefinition:
                    var definition = metadata.GetTypeDefinition((TypeDefinitionHandle)handle);
                    return Join(metadata.GetString(definition.Namespace), metadata.GetString(definition.Name));
                case HandleKind.TypeReference:
                    var reference = metadata.GetTypeReference((TypeReferenceHandle)handle);
                    return Join(metadata.GetString(reference.Namespace), metadata.GetString(reference.Name));
                default:
                    return handle.Kind.ToString();
            }
        }

        private static string Join(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;

        /// <summary>Spells signature types as full names; enough to compare the constructor's parameter list.</summary>
        private sealed class TypeNameProvider : ISignatureTypeProvider<string, object?>
        {
            public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
            public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => TypeName(reader, handle);
            public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => TypeName(reader, handle);
            public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => "typespec";
            public string GetSZArrayType(string elementType) => elementType + "[]";
            public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[*]";
            public string GetByReferenceType(string elementType) => elementType + "&";
            public string GetPointerType(string elementType) => elementType + "*";
            public string GetPinnedType(string elementType) => elementType;
            public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
                genericType + "<" + string.Join(",", typeArguments) + ">";
            public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
            public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
            public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
            public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
        }
    }
}
