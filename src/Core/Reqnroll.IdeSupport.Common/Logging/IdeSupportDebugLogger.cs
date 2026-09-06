using System;
using System.Diagnostics;

namespace Reqnroll.IdeSupport.Common.Logging;

/// <summary>IdeSupportDebugLogger</summary>
public class IdeSupportDebugLogger : IIdeSupportLogger
{
#if DEBUG
    /// <summary>The default trace level used in debug builds (Verbose).</summary>
    public const TraceLevel DefaultDebugTraceLevel = TraceLevel.Verbose;
#else
    public const TraceLevel DefaultDebugTraceLevel = TraceLevel.Off;
#endif
    /// <summary>Gets the minimum trace level that will be written to the debug output.</summary>
    public TraceLevel Level { get; }

    /// <summary>Initializes a new instance of the <see cref="IdeSupportDebugLogger"/> class.</summary>
    public IdeSupportDebugLogger(TraceLevel level = DefaultDebugTraceLevel)
    {
        Level = level;
        var env = Environment.GetEnvironmentVariable("REQNROLLVS_DEBUG");
        if (env != null)
        {
            if (env.Equals("1") || env.Equals("true", StringComparison.InvariantCultureIgnoreCase))
                Level = TraceLevel.Verbose;
            else if (Enum.TryParse<TraceLevel>(env, true, out var envLevel)) Level = envLevel;
        }
    }

    /// <summary>Writes the message to the debug output if its level is within the configured threshold.</summary>
    public void Log(LogMessage message)
    {
        Debug.WriteLineIf(message.Level <= Level, $"{LogLineFormatter.FormatPreamble(message)}: {message.Message}",
            "ReqnrollVs");
    }
}
