using System.Text;
using dnlib.DotNet;

namespace ReqnrollConnector.SourceDiscovery;

/// <summary>
/// Builds the documentation-comment ID (<c>M:Namespace.Type.Method(ParamType,...)</c>) of a method
/// from its metadata, in exactly the form Roslyn's <c>ISymbol.GetDocumentationCommentId()</c> produces,
/// so the server and IDE clients can resolve it with <c>DocumentationCommentId.GetSymbolsForDeclarationId</c>.
/// </summary>
/// <remarks>
/// Works from dnlib metadata rather than reflection: it needs no type loading, so it behaves the
/// same in the .NET and .NET Framework connectors and cannot fail on a parameter type whose
/// assembly is missing.
/// <para>
/// Returns <see langword="null"/> — no identity rather than a guessed one — for anything Reqnroll
/// does not accept as a binding method and so never needs an identity: generic methods, methods of
/// generic types (binding classes cannot be generic), special-name methods (constructors,
/// accessors, operators), and parameter types without a stable ID form here (function pointers,
/// generic parameters, constructed generic types nested in other types). Closed generic
/// parameter types such as <c>List&lt;Book&gt;</c> or <c>int?</c> are supported: step argument
/// transformations produce them routinely.
/// </para>
/// </remarks>
public static class DocumentationCommentIdBuilder
{
    /// <summary>Returns the documentation-comment ID of <paramref name="method"/>, or <see langword="null"/> when it has none here.</summary>
    public static string? Build(MethodDef? method)
    {
        if (method?.DeclaringType == null || method.HasGenericParameters || method.IsSpecialName ||
            method.IsConstructor || HasGenericEnclosingType(method.DeclaringType))
            return null;

        var builder = new StringBuilder("M:");
        AppendTypeDefinitionName(builder, method.DeclaringType);
        builder.Append('.').Append(EscapeMemberName(method.Name));

        var parameters = method.MethodSig?.Params;
        if (parameters == null)
            return null;

        if (parameters.Count > 0)
        {
            builder.Append('(');
            for (var i = 0; i < parameters.Count; i++)
            {
                if (i > 0) builder.Append(',');
                if (!TryAppendTypeSig(builder, parameters[i]))
                    return null;
            }
            builder.Append(')');
        }

        return builder.ToString();
    }

    private static bool HasGenericEnclosingType(TypeDef type)
    {
        for (var current = type; current != null; current = current.DeclaringType)
            if (current.HasGenericParameters)
                return true;
        return false;
    }

    // Dots inside a member name (explicit interface implementations) become '#' in an ID.
    private static string EscapeMemberName(string name) => name.Replace('.', '#');

    private static void AppendTypeDefinitionName(StringBuilder builder, TypeDef type)
    {
        if (type.DeclaringType != null)
        {
            AppendTypeDefinitionName(builder, type.DeclaringType);
            builder.Append('.').Append(type.Name.String);
            return;
        }

        if (!UTF8String.IsNullOrEmpty(type.Namespace))
            builder.Append(type.Namespace.String).Append('.');
        builder.Append(type.Name.String);
    }

    private static bool TryAppendTypeSig(StringBuilder builder, TypeSig? sig)
    {
        switch (sig)
        {
            case null:
                return false;

            // Custom modifiers and pinning are not part of a documentation-comment ID.
            case ModifierSig modifier:
                return TryAppendTypeSig(builder, modifier.Next);
            case PinnedSig pinned:
                return TryAppendTypeSig(builder, pinned.Next);

            case ByRefSig byRef:
                if (!TryAppendTypeSig(builder, byRef.Next)) return false;
                builder.Append('@');
                return true;

            case PtrSig pointer:
                if (!TryAppendTypeSig(builder, pointer.Next)) return false;
                builder.Append('*');
                return true;

            case SZArraySig vector:
                if (!TryAppendTypeSig(builder, vector.Next)) return false;
                builder.Append("[]");
                return true;

            case ArraySig array:
                if (!TryAppendTypeSig(builder, array.Next)) return false;
                builder.Append('[');
                for (var dimension = 0; dimension < array.Rank; dimension++)
                {
                    if (dimension > 0) builder.Append(',');
                    builder.Append("0:");
                }
                builder.Append(']');
                return true;

            case GenericInstSig generic:
                return TryAppendGenericInstance(builder, generic);

            case TypeDefOrRefSig typeDefOrRef:
                return TryAppendTypeName(builder, typeDefOrRef.TypeDefOrRef, stripArity: false);

            // Generic parameters, function pointers, sentinels: not produced by an accepted binding.
            default:
                return false;
        }
    }

    private static bool TryAppendGenericInstance(StringBuilder builder, GenericInstSig generic)
    {
        var genericType = generic.GenericType?.TypeDefOrRef;
        if (genericType == null || genericType.DeclaringType != null)
            return false;

        if (!TryAppendTypeName(builder, genericType, stripArity: true))
            return false;

        builder.Append('{');
        for (var i = 0; i < generic.GenericArguments.Count; i++)
        {
            if (i > 0) builder.Append(',');
            if (!TryAppendTypeSig(builder, generic.GenericArguments[i]))
                return false;
        }
        builder.Append('}');
        return true;
    }

    private static bool TryAppendTypeName(StringBuilder builder, ITypeDefOrRef? type, bool stripArity)
    {
        if (type == null || type is TypeSpec)
            return false;

        if (type.DeclaringType != null)
        {
            if (!TryAppendTypeName(builder, type.DeclaringType, stripArity: false))
                return false;
            builder.Append('.');
        }
        else if (!string.IsNullOrEmpty(type.Namespace))
        {
            builder.Append(type.Namespace).Append('.');
        }

        var name = type.TypeName;
        if (stripArity)
        {
            var tick = name.IndexOf('`');
            if (tick >= 0)
                name = name.Substring(0, tick);
        }
        else if (name.IndexOf('`') >= 0)
        {
            // An open generic type outside a GenericInstSig has no ID form we emit.
            return false;
        }

        builder.Append(name);
        return true;
    }
}
