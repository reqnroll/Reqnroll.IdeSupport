using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace Reqnroll.IdeSupport.Common.Telemetry;

/// <summary>
/// Reduces an exception's stack to a privacy-safe, bounded attribution string for telemetry (issue #620).
/// <para>
/// Only frames whose declaring type lives in a <c>Reqnroll.IdeSupport</c> namespace are named, as
/// <c>Namespace.Type.Method</c>: no file paths, line/column numbers, parameter lists or generic
/// arguments (those could carry user type names) are ever read. Compiler-generated async/lambda/local-function
/// members are normalised to the member that declares them (<c>Method</c>, <c>Method{lambda}</c>,
/// <c>Method{local}</c>). Every run of frames from any other assembly (BCL, third party, user code)
/// is collapsed to a single <see cref="ExternalPlaceholder"/> entry, so nothing outside this product is named.
/// </para>
/// <para>
/// Only the exception's own stack is read, never its message or inner exceptions. Reusable by any
/// host that wants to attach the same attribution to its own exception events (#621, #845).
/// </para>
/// </summary>
public static class ExceptionStackSanitizer
{
    /// <summary>Entry standing in for a run of consecutive frames from non-Reqnroll.IdeSupport assemblies.</summary>
    public const string ExternalPlaceholder = "[external]";

    /// <summary>Separator between frames in the returned string (innermost frame first).</summary>
    public const char FrameSeparator = '\n';

    /// <summary>Default maximum number of entries (Reqnroll frames plus placeholders) returned.</summary>
    public const int DefaultMaxFrames = 8;

    /// <summary>Hard cap on the length of the returned string.</summary>
    public const int MaxLength = 1024;

    private const string ProductNamespace = "Reqnroll.IdeSupport";

    /// <summary>
    /// Returns up to <paramref name="maxFrames"/> sanitized frames joined by <see cref="FrameSeparator"/>,
    /// or <see langword="null"/> when the exception was never thrown, no frame belongs to this product,
    /// or stack metadata is unavailable. Never throws.
    /// </summary>
    public static string Sanitize(Exception exception, int maxFrames = DefaultMaxFrames)
    {
        if (exception == null || maxFrames < 1)
            return null;

        try
        {
            var frames = new StackTrace(exception, fNeedFileInfo: false).GetFrames();
            if (frames == null)
                return null;

            var entries = new List<string>();
            var sawProductFrame = false;
            foreach (var frame in frames)
            {
                var method = frame.GetMethod();
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
            var count = 0;
            foreach (var entry in entries)
            {
                if (count == maxFrames)
                    break;
                var added = (count == 0 ? 0 : 1) + entry.Length;
                if (result.Length + added > MaxLength)
                    break;
                if (count > 0)
                    result.Append(FrameSeparator);
                result.Append(entry);
                count++;
            }
            return count == 0 ? null : result.ToString();
        }
        catch (Exception)
        {
            // Stack metadata can be trimmed or unavailable; telemetry must never fail the caller.
            return null;
        }
    }

    private static string FormatProductFrame(MethodBase method)
    {
        var declaring = method.DeclaringType;
        var ns = declaring?.Namespace;
        if (declaring == null || ns == null ||
            !(ns == ProductNamespace || ns.StartsWith(ProductNamespace + ".", StringComparison.Ordinal)))
            return null;

        // Type chain, outermost first, dropping compiler-generated segments (<>c, <Foo>d__3, DisplayClass).
        var typeNames = new List<string>();
        for (var type = declaring; type != null; type = type.DeclaringType)
        {
            if (!type.Name.StartsWith("<", StringComparison.Ordinal))
                typeNames.Insert(0, StripArity(type.Name));
        }

        var methodName = NormalizeMethodName(method.Name, declaring.Name);
        if (typeNames.Count == 0)
            return ns + "." + methodName;
        return ns + "." + string.Join(".", typeNames) + "." + methodName;
    }

    private static string NormalizeMethodName(string methodName, string declaringTypeName)
    {
        // Async/iterator state machine: type <Foo>d__3, method MoveNext -> Foo.
        if (declaringTypeName.StartsWith("<", StringComparison.Ordinal) && !methodName.StartsWith("<", StringComparison.Ordinal))
        {
            var fromType = Between(declaringTypeName);
            if (fromType.Length > 0)
                return fromType;
        }

        if (methodName.StartsWith("<", StringComparison.Ordinal))
        {
            var inner = Between(methodName);
            if (inner.Length == 0)
                return "{anonymous}";
            if (methodName.IndexOf(">b__", StringComparison.Ordinal) >= 0)
                return inner + "{lambda}";
            if (methodName.IndexOf(">g__", StringComparison.Ordinal) >= 0)
                return inner + "{local}";
            return inner;
        }

        return methodName;
    }

    // "<Foo>b__0_1" -> "Foo"
    private static string Between(string compilerName)
    {
        var end = compilerName.IndexOf('>');
        return end > 1 ? compilerName.Substring(1, end - 1) : string.Empty;
    }

    // "Foo`2" -> "Foo"
    private static string StripArity(string typeName)
    {
        var tick = typeName.IndexOf('`');
        return tick > 0 ? typeName.Substring(0, tick) : typeName;
    }
}
