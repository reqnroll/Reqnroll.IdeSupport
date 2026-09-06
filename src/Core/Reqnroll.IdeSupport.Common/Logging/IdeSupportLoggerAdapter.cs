#nullable enable

using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Reqnroll.IdeSupport.Common.Logging;

/// <summary>
/// Canonical <see cref="TraceLevel"/> &lt;-&gt; <see cref="LogLevel"/> mapping, shared by every
/// place that bridges <see cref="IIdeSupportLogger"/> (our app-level logging API) onto
/// <see cref="Microsoft.Extensions.Logging"/>'s <c>ILogger</c>/<c>ILogger&lt;T&gt;</c> abstractions.
/// </summary>
public static class IdeSupportLogLevelConverter
{
    /// <summary>Converts a <see cref="Microsoft.Extensions.Logging"/> <see cref="LogLevel"/> to the equivalent <see cref="TraceLevel"/>.</summary>
    public static TraceLevel ToTraceLevel(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug    => TraceLevel.Verbose,
        LogLevel.Information                => TraceLevel.Info,
        LogLevel.Warning                    => TraceLevel.Warning,
        LogLevel.Error or LogLevel.Critical => TraceLevel.Error,
        _                                   => TraceLevel.Off,
    };

    /// <summary>Converts a <see cref="TraceLevel"/> to the equivalent <see cref="Microsoft.Extensions.Logging"/> <see cref="LogLevel"/>.</summary>
    public static LogLevel ToLogLevel(TraceLevel level) => level switch
    {
        TraceLevel.Off     => LogLevel.None,
        TraceLevel.Error   => LogLevel.Error,
        TraceLevel.Warning => LogLevel.Warning,
        TraceLevel.Info    => LogLevel.Information,
        TraceLevel.Verbose => LogLevel.Trace,
        _                  => LogLevel.Warning,
    };
}

/// <summary>Adapts a single <see cref="Microsoft.Extensions.Logging"/> category onto an <see cref="IIdeSupportLogger"/> sink.</summary>
public sealed class IdeSupportLoggerAdapter : ILogger
{
    private readonly string _shortCategoryName;
    private readonly IIdeSupportLogger _logger;

    /// <summary>Initializes a new instance of the <see cref="IdeSupportLoggerAdapter"/> class.</summary>
    public IdeSupportLoggerAdapter(string categoryName, IIdeSupportLogger logger)
    {
        // Shortened to the simple type name (issue #626): categoryName is usually a
        // fully-qualified name (e.g. from ILogger<T>), and the direct IIdeSupportLogger call
        // sites already render just a short source name, not a namespace-qualified one.
        _shortCategoryName = ShortenCategoryName(categoryName);
        _logger = logger;
    }

    /// <summary>Determines whether messages at <paramref name="logLevel"/> would be recorded by the underlying logger.</summary>
    public bool IsEnabled(LogLevel logLevel) => _logger.IsLogging(IdeSupportLogLevelConverter.ToTraceLevel(logLevel));

    /// <summary>Writes a log entry for the specified category.</summary>
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        // No method-name equivalent is available through ILogger - CallerMethod is left empty so
        // LogLineFormatter renders the category alone rather than "Category." with nothing after it.
        _logger.Log(new LogMessage(IdeSupportLogLevelConverter.ToTraceLevel(logLevel), formatter(state, exception),
            CallerMethod: string.Empty, exception, Source: _shortCategoryName));
    }

    private static string ShortenCategoryName(string categoryName)
    {
        var lastDot = categoryName.LastIndexOf('.');
        return lastDot >= 0 && lastDot < categoryName.Length - 1 ? categoryName.Substring(lastDot + 1) : categoryName;
    }

    /// <summary>Begins a logical operation scope.</summary>
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

/// <summary>
/// Bridges a single <see cref="IIdeSupportLogger"/> sink onto the standard
/// <see cref="ILoggerFactory"/>/<see cref="ILogger{TCategoryName}"/> abstractions, so that
/// consumers can be written against <c>ILogger&lt;T&gt;</c> while still writing through the same
/// file/debug sink as everything else using <see cref="IIdeSupportLogger"/> directly.
/// </summary>
public sealed class IdeSupportLoggerFactory : ILoggerFactory
{
    private readonly IIdeSupportLogger _logger;

    /// <summary>Initializes a new instance of the <see cref="IdeSupportLoggerFactory"/> class.</summary>
    public IdeSupportLoggerFactory(IIdeSupportLogger logger) => _logger = logger;

    /// <summary>Creates an <see cref="ILogger"/> for the given category, backed by the shared <see cref="IIdeSupportLogger"/> sink.</summary>
    public ILogger CreateLogger(string categoryName) => new IdeSupportLoggerAdapter(categoryName, _logger);

    // Single sink by design - IIdeSupportLogger already fans out via IdeSupportCompositeLogger when needed.
    /// <summary>No-op: additional providers are not supported since there is a single fixed sink.</summary>
    public void AddProvider(ILoggerProvider provider) { }

    /// <summary>No-op: this factory holds no disposable resources of its own.</summary>
    public void Dispose() { }
}
