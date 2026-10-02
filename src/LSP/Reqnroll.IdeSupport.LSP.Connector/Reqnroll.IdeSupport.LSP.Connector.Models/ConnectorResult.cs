#nullable disable
using System.Collections.Generic;

namespace Reqnroll.IdeSupport.LSP.Connector.Models;

/// <summary>ConnectorResult</summary>
public abstract class ConnectorResult
{
    /// <summary>Gets or sets the connector type.</summary>
    public string ConnectorType { get; set; }
    /// <summary>
    /// The OS process ID of the Connector process that produced this result (issue #637) — set by
    /// the LSP server after the process exits, from <c>ProcessHelper.RunProcessResult.ProcessId</c>,
    /// not part of the connector's own JSON output (same pattern as <see cref="ConnectorType"/>
    /// above, which the server also overwrites after deserializing). Lets a reader of the server's
    /// own log correlate a "Discovery complete"/"Discovery failed" line with the matching
    /// <c>reqnroll-*-connector-*-{pid}.log</c> file.
    /// </summary>
    public int? ConnectorProcessId { get; set; }
    /// <summary>Gets or sets the reqnroll version.</summary>
    public string ReqnrollVersion { get; set; }
    /// <summary>Gets or sets the error message.</summary>
    public string ErrorMessage { get; set; }
    /// <summary>Gets or sets the is failed.</summary>
    public bool IsFailed => !string.IsNullOrWhiteSpace(ErrorMessage);
    /// <summary>Gets or sets the log messages.</summary>
    public string[] LogMessages { get; set; }
    /// <summary>Gets or sets the warnings.</summary>
    public string[] Warnings { get; set; }
    /// <summary>Gets or sets the telemetry properties.</summary>
    public Dictionary<string, object> TelemetryProperties { get; set; }
}
