using Microsoft.Extensions.Logging;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.LSP.Server.Logging;

/// <summary>
/// Bridges OmniSharp's own internal <see cref="Microsoft.Extensions.Logging"/> diagnostics
/// (request dispatch, DryIoc, JSON-RPC plumbing — whatever the library itself logs via
/// <c>ILogger&lt;T&gt;</c>) into a dedicated Reqnroll log file, in addition to the existing
/// <c>window/logMessage</c> notification (<c>AddLanguageProtocolLogging()</c>) that only reaches
/// the connected client.
/// </summary>
/// <remarks>
/// Without this, OmniSharp-internal diagnostics exist only as a transient client notification —
/// if the user doesn't think to copy their editor's Output panel, that information is gone by the
/// time we're asked to look at a bug report. Writes to a separate <c>reqnroll-*-protocol-*.log</c>
/// file (not the main server log) so two independent writers never append to the same file
/// concurrently, and gated by its own <c>--protocol-log-level</c> rather than the main
/// <c>--log-level</c> — the two are deliberately decoupled (F41 follow-up): a user turning up file
/// logging for our own app-level diagnosis shouldn't also flood their editor's Output panel with
/// library internals, and vice versa.
/// </remarks>
public sealed class ProtocolLoggerProvider : ILoggerProvider
{
    private readonly IIdeSupportLogger _logger;

    /// <summary>Creates the provider, building the default protocol-log file/debug sink for the given client IDE and log level.</summary>
    public ProtocolLoggerProvider(string? clientIde, TraceLevel protocolLogLevel)
        : this(BuildDefaultLogger(clientIde, protocolLogLevel))
    {
    }

    /// <summary>Test seam: bypasses the real file/debug sinks.</summary>
    internal ProtocolLoggerProvider(IIdeSupportLogger logger)
    {
        _logger = logger;
    }

    private static IIdeSupportLogger BuildDefaultLogger(string? clientIde, TraceLevel protocolLogLevel)
    {
        var idePrefix = clientIde switch
                {
                    "visualstudio" => "vs",
                    "vscode"       => "vscode",
                    "rider"        => "rider",
                    _              => "lsp"
                };
        return new IdeSupportCompositeLogger()
            .Add(new IdeSupportDebugLogger())
            .Add(new SynchronousFileLogger(idePrefix, "protocol", protocolLogLevel));
    }

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => new IdeSupportLoggerAdapter(categoryName, _logger);

    /// <inheritdoc/>
    public void Dispose()
    {
    }
}
