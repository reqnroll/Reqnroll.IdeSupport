using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Reqnroll.IdeSupport.TestReporter.Common;

/// <summary>
/// Minimal single-object JSON writer for the reporter wire format: flat objects with string, number,
/// bool and string-array fields, one object per line. Hand-rolled on purpose — the reporters that use
/// this must not take a JSON dependency into the process they load into (a test runner), and the
/// vocabulary is small enough that a serializer would be more code than this.
/// </summary>
public sealed class NdjsonWriter
{
    private readonly StringBuilder _sb = new("{");
    private bool _hasField;

    public static NdjsonWriter Object(string type)
        => new NdjsonWriter().Field("type", type);

    public NdjsonWriter Field(string name, string? value)
    {
        Separator().Name(name);
        if (value is null) _sb.Append("null");
        else WriteString(value);
        return this;
    }

    public NdjsonWriter Field(string name, long value)
    {
        Separator().Name(name);
        _sb.Append(value.ToString(CultureInfo.InvariantCulture));
        return this;
    }

    public NdjsonWriter Field(string name, double value)
    {
        Separator().Name(name);
        // "R" keeps round-trip precision on netstandard2.0; NaN/Infinity aren't valid JSON, so they degrade to null.
        if (double.IsNaN(value) || double.IsInfinity(value)) _sb.Append("null");
        else _sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
        return this;
    }

    public NdjsonWriter Field(string name, bool value)
    {
        Separator().Name(name);
        _sb.Append(value ? "true" : "false");
        return this;
    }

    public NdjsonWriter Field(string name, IEnumerable<string> values)
    {
        Separator().Name(name);
        _sb.Append('[');
        var first = true;
        foreach (var value in values)
        {
            if (!first) _sb.Append(',');
            first = false;
            WriteString(value);
        }
        _sb.Append(']');
        return this;
    }

    /// <summary>The finished object followed by a newline — one NDJSON record.</summary>
    public string ToLine() => _sb.ToString() + "}\n";

    private NdjsonWriter Separator()
    {
        if (_hasField) _sb.Append(',');
        _hasField = true;
        return this;
    }

    private void Name(string name)
    {
        WriteString(name);
        _sb.Append(':');
    }

    private void WriteString(string value)
    {
        _sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': _sb.Append("\\\""); break;
                case '\\': _sb.Append("\\\\"); break;
                case '\n': _sb.Append("\\n"); break;
                case '\r': _sb.Append("\\r"); break;
                case '\t': _sb.Append("\\t"); break;
                case '\b': _sb.Append("\\b"); break;
                case '\f': _sb.Append("\\f"); break;
                default:
                    if (c < 0x20)
                        _sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        _sb.Append(c);
                    break;
            }
        }
        _sb.Append('"');
    }
}
