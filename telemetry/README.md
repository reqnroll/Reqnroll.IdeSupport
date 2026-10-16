# Telemetry dashboard assets

Queries, workbooks and alerts for the Reqnroll.IdeSupport Application Insights resources (issue #1190).
The event catalog and property schema live in
`src/Core/Reqnroll.IdeSupport.Common/Telemetry/TelemetryEvents.cs`.

## Layout

- `queries/*.kql` - one query per file, with the assumptions and verification status in a header comment.

## Trying a query against the dev resource group

1. Set `REQNROLL_DEBUG_TELEMETRY_CONNECTION_STRING` to the dev Application Insights connection string
   before launching the IDE (Visual Studio, VS Code or Rider). It redirects all three extensions.
2. Use the extension, then run the query in the dev Log Analytics workspace (Logs blade).
   Ingestion typically lags a few minutes.

The queries target the workspace-based `AppEvents` table; the header of each file says how to adapt
it to the classic `customEvents` table.
