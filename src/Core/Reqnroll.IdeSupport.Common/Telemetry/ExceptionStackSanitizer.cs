using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace Reqnroll.IdeSupport.Common.Telemetry;

/// <summary>
/// Reduces an exception's stack to a privacy-safe, bounded attribution string for telemetry (issue #620).
/// <para>
/// Only frames whose declaring type is a product type (see <see cref="IsProductType"/>: namespace AND
/// assembly both in the <c>Reqnroll.IdeSupport</c> tree) are named, as <c>Namespace.Type.Method</c>: no file
/// paths, line/column numbers, parameter lists, generic arguments or assembly names are ever read or emitted.
/// Compiler-generated async/lambda/local-function members are normalised to the member that declares them
/// (<c>Method</c>, <c>Method{lambda}</c>, <c>Method{local}</c>). Every run of frames from any other assembly
/// (BCL, third party, user code) is collapsed to a single <see cref="ExternalPlaceholder"/> entry.
/// </para>
/// <para>
/// Only the exception's own stack is read, never its message, type name or inner exceptions. Because the
/// stack is read as the runtime reports it, frames the JIT inlined are absent, and the placeholder collapse
/// plus the entry cap can push deeper product frames out. Reusable by any host that wants the same
/// attribution on its own exception events (#621, #845).
/// </para>
/// </summary>
public static class ExceptionStackSanitizer
{
    /// <summary>Entry standing in for a run of consecutive frames from non-product assemblies.</summary>
    public const string ExternalPlaceholder = "[external]";

    /// <summary>Separator between frames in the returned string (innermost frame first).</summary>
    public const char FrameSeparator = '\n';

    /// <summary>Default maximum number of entries (product frames plus placeholders) returned.</summary>
    public const int DefaultMaxFrames = 8;

    /// <summary>Hard cap on the length of the returned string.</summary>
    public const int MaxLength = 1024;

    private const string ProductName = "Reqnroll.IdeSupport";

    private static readonly char[] GenericOrParameterStart = { '<', '(' };

    /// <summary>
    /// <see langword="true"/> when <paramref name="type"/> is declared in a <c>Reqnroll.IdeSupport</c> namespace
    /// <b>and</b> in a <c>Reqnroll.IdeSupport</c> assembly (exact name or <c>Reqnroll.IdeSupport.*</c>), so a
    /// foreign assembly cannot impersonate the product by reusing its namespace, and
    /// <c>Reqnroll.IdeSupportLookalike</c> is rejected. The single definition of "product code" for telemetry.
    /// </summary>
    public static bool IsProductType(Type type)
    {
        try
        {
            return type != null &&
                   IsProductName(type.Namespace) &&
                   IsProductName(type.Assembly.GetName().Name);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsProductName(string name) =>
        name != null && (name == ProductName || name.StartsWith(ProductName + ".", StringComparison.Ordinal));

    /// <summary>
    /// Convenience for <c>Capture(exception)?.Sanitize(maxFrames)</c>: up to <paramref name="maxFrames"/> sanitized
    /// frames joined by <see cref="FrameSeparator"/>, or <see langword="null"/> when the exception was never thrown,
    /// no frame belongs to the product, or stack metadata is unavailable. Never throws.
    /// </summary>
    public static string Sanitize(Exception exception, int maxFrames = DefaultMaxFrames) =>
        Capture(exception)?.Sanitize(maxFrames);

    /// <summary>
    /// Walks the exception's stack once. The result yields both <see cref="CapturedStack.Source"/> and (lazily)
    /// the sanitized frames, so callers can skip the formatting work when they will not use it.
    /// <see langword="null"/> when there is no stack or no product frame. Never throws.
    /// </summary>
    public static CapturedStack Capture(Exception exception)
    {
        if (exception == null)
            return null;
        try
        {
            var frames = new StackTrace(exception, fNeedFileInfo: false).GetFrames();
            if (frames == null)
                return null;

            var methods = new List<MethodBase>(frames.Length);
            var sawProduct = false;
            foreach (var frame in frames)
            {
                var method = frame.GetMethod();
                methods.Add(method);
                if (!sawProduct && method?.DeclaringType != null && IsProductType(method.DeclaringType))
                    sawProduct = true;
            }
            return sawProduct ? new CapturedStack(methods) : null;
        }
        catch (Exception)
        {
            // Stack metadata can be trimmed or unavailable; telemetry must never fail the caller.
            return null;
        }
    }

    /// <summary>The frames of one exception's stack, ready to be reduced to telemetry-safe text.</summary>
    public sealed class CapturedStack
    {
        private readonly List<MethodBase> _methods;

        internal CapturedStack(List<MethodBase> methods) => _methods = methods;

        /// <summary>
        /// Simple name (no namespace) of the class holding the topmost product frame; compiler-generated
        /// nested types fold into their declaring class.
        /// </summary>
        public string Source
        {
            get
            {
                foreach (var method in _methods)
                {
                    var type = method?.DeclaringType;
                    if (type == null || !IsProductType(type))
                        continue;
                    while (type.IsNested && type.Name.StartsWith("<", StringComparison.Ordinal) && type.DeclaringType != null)
                        type = type.DeclaringType;
                    return type.Name;
                }
                return null;
            }
        }

        /// <summary>The sanitized stack; see <see cref="ExceptionStackSanitizer"/>. <see langword="null"/> on any failure.</summary>
        public string Sanitize(int maxFrames = DefaultMaxFrames)
        {
            if (maxFrames < 1)
                return null;
            try
            {
                var entries = new List<string>();
                var sawProductFrame = false;
                foreach (var method in _methods)
                {
                    if (entries.Count >= maxFrames)
                        break;
                    var name = method == null ? null : FormatProductFrame(method);
                    if (name == null)
                    {
                        if (entries.Count == 0 || entries[entries.Count - 1] != ExternalPlaceholder)
                            entries.Add(ExternalPlaceholder);
                    }
                    else
                    {
                        sawProductFrame = true;
                        entries.Add(name);
                    }
                }

                if (!sawProductFrame)
                    return null;

                var result = new StringBuilder();
                foreach (var entry in entries)
                {
                    var added = (result.Length == 0 ? 0 : 1) + entry.Length;
                    if (result.Length + added > MaxLength)
                        break;
                    if (result.Length > 0)
                        result.Append(FrameSeparator);
                    result.Append(entry);
                }
                return result.Length == 0 ? null : result.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    private static string FormatProductFrame(MethodBase method)
    {
        var declaring = method.DeclaringType;
        if (declaring == null || !IsProductType(declaring))
            return null;

        // Type chain, outermost first, dropping compiler-generated segments (<>c, <Foo>d__3, DisplayClass).
        var typeNames = new List<string>();
        for (var type = declaring; type != null; type = type.DeclaringType)
        {
            if (!type.Name.StartsWith("<", StringComparison.Ordinal))
                typeNames.Insert(0, Sanitized(StripArity(type.Name)));
        }

        var methodName = NormalizeMethodName(method.Name, declaring.Name);
        var prefix = declaring.Namespace + "." + (typeNames.Count == 0 ? string.Empty : string.Join(".", typeNames) + ".");
        return prefix + methodName;
    }

    private static string NormalizeMethodName(string methodName, string declaringTypeName)
    {
        // Compiler-named member (lambda, local function, iterator/async state machine).
        if (methodName.StartsWith("<", StringComparison.Ordinal))
            return FromCompilerName(methodName);
        if (declaringTypeName.StartsWith("<", StringComparison.Ordinal))
        {
            // State machine <Foo>d__3 (method MoveNext) or a lambda display class: report the declaring member.
            var fromType = FromCompilerName(declaringTypeName);
            if (fromType != "{anonymous}")
                return fromType;
        }

        if (methodName == ".ctor")
            return "{ctor}";
        if (methodName == ".cctor")
            return "{cctor}";

        // Explicit interface implementation ("Ns.IFoo<T>.Handle"): keep only the member, never the interface
        // or its generic arguments.
        if (methodName.IndexOf('.') > 0 || methodName.IndexOf('<') >= 0)
        {
            var last = methodName.LastIndexOf('.');
            methodName = last >= 0 ? methodName.Substring(last + 1) : methodName;
        }
        return Sanitized(methodName);
    }

    // "<Foo>b__0_1" -> "Foo{lambda}"; "<<Foo>b__0>g__Local|1_0" -> "Foo{local}"; "<Foo>d__3" -> "Foo"
    private static string FromCompilerName(string compilerName)
    {
        var start = 0;
        while (start < compilerName.Length && compilerName[start] == '<')
            start++;
        var end = compilerName.IndexOf('>', start);
        var inner = end > start ? Sanitized(compilerName.Substring(start, end - start)) : string.Empty;
        if (inner.Length == 0)
            return "{anonymous}";
        if (compilerName.IndexOf(">g__", StringComparison.Ordinal) >= 0)
            return inner + "{local}";
        if (compilerName.IndexOf(">b__", StringComparison.Ordinal) >= 0)
            return inner + "{lambda}";
        return inner;
    }

    // Final guard: cut at the first '<' or '(' (generic args / parameter lists) and keep only identifier characters.
    private static string Sanitized(string name)
    {
        var cut = name.IndexOfAny(GenericOrParameterStart);
        if (cut >= 0)
            name = name.Substring(0, cut);
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
                sb.Append(c);
        }
        return sb.ToString();
    }

    // "Foo`2" -> "Foo"
    private static string StripArity(string typeName)
    {
        var tick = typeName.IndexOf('`');
        return tick > 0 ? typeName.Substring(0, tick) : typeName;
    }
}
