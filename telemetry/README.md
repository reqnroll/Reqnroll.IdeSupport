# Telemetry dashboard assets

Queries (and, later, workbooks and alerts) for the Reqnroll.IdeSupport Application Insights resources
(issue #1190). The event catalog and property schema live in
`src/Core/Reqnroll.IdeSupport.Common/Telemetry/TelemetryEvents.cs`; every event lands in one Application
Insights resource per environment (dev / prod), with the event name as its identity.

## Queries

All files are in `queries/`. Each one starts with a header comment that lists its assumptions, definitions and
**verification status**.

| File | What it reports | Source events |
|---|---|---|
| `daily-usage.kql` | Daily active users, sessions started and sessions per user, by `IdeClient`, `OperatingSystem` and a derived RID (`win-x64`, `osx-arm64`, ...). | `ServerSessionStarted`, `FeatureUsageSummary`, `ServerSessionEnded` |
| `session-duration.kql` | Median and P90 session length by day and `IdeClient`, plus sessions that never sent an end event (crash proxy). | `ServerSessionStarted`, `ServerSessionEnded` |
| `operation-latency.kql` | P50 / P95 / max latency per server operation and `IdeClient`, grouped by a derived `Category` (Request, DocumentSync, Internal, TestOutcomes). | `PerfSample` (sampled, ~5% by default) |
| `error-rates.kql` | Unhandled exceptions (server and client origin) and language-server failure events per day, with affected users and events per 100 sessions. | `UnhandledException`, `ServerStartFailed`, `ServerExitedUnexpectedly`, `ServerRestarted` |

### Things to know when reading the results

- **`PerfSample` is sampled.** `Samples` is a confidence measure for the percentiles, not a usage count. For usage use
  the `* command executed` events and `FeatureUsageSummary`.
- **RID is derived** from `OperatingSystem` and `Architecture`; the client never sends a RID.
- **Exceptions are events, not rows in the Application Insights `exceptions` table.** Client-side exceptions
  (`ExceptionOrigin = "Client"`) carry no `SessionId`, so they cannot be attributed to a session; see the error-rates header.
- **Verification status.** All four queries were run on 2026-10-15/16 against the dev resource using the *classic*
  table and column names (see below) with Visual Studio and Rider data on Windows. Not yet verified: the `AppEvents`
  (workspace) forms, VS Code, macOS/Linux, and the `ServerStartFailed` / `ServerExitedUnexpectedly` / `ServerRestarted` paths.

## Two table-name flavours

The same data has two names depending on where you open **Logs**:

| Where you open Logs | Events table | Time | Properties | User |
|---|---|---|---|---|
| Log Analytics **workspace** (the files in `queries/` use these) | `AppEvents` | `TimeGenerated` | `Properties` | `UserId` |
| Application Insights **resource** (Monitoring > Logs) | `customEvents` | `timestamp` | `customDimensions` | `user_Id` |

To run a file in the resource-scoped view, replace `AppEvents` with `customEvents`, `TimeGenerated` with `timestamp`,
`Properties` with `customDimensions`, `UserId` with `user_Id` and `Name` with `name`.

## Trying a query against the dev resource group

1. Set `REQNROLL_DEBUG_TELEMETRY_CONNECTION_STRING` to the **dev** Application Insights connection string before
   launching the IDE (Visual Studio, VS Code or Rider). It redirects all three extensions; it needs an
   `InstrumentationKey=` part or it is ignored. Do not use the prod connection string for testing.
2. Optionally set `REQNROLL_PERF_TELEMETRY_SAMPLE=1` to emit every `PerfSample` instead of ~5% (restart the IDE).
3. Use the extension for a while, then close the IDE normally (so `ServerSessionEnded` is sent).
4. Run the query in Logs. Ingestion typically lags a few minutes.

## Adding the queries to a workspace (for whoever owns the Azure subscription)

This is a one-time setup per environment (dev, then prod). It needs the **Log Analytics Contributor** role (or
Contributor) on the environment's resource group, and **Log Analytics Reader** to run the queries. Portal screens
move around a little between releases, so treat the labels as a guide.

1. In the Azure portal, open the environment's **Log Analytics workspace** (for dev:
   `reqnroll-ide-appinsights-dev-ws`, linked from the Application Insights resource's Overview page).
2. Select **Logs**, and close the "Queries" pop-up if it appears.
3. Open a query file from this folder, copy everything **below the header comments** (or all of it; comments are valid KQL),
   and paste it into the editor. Set the scope to the workspace and check that **Time range** says *Set in query*.
4. Click **Run** and confirm it returns rows or a sensible empty result. If a column comes back empty, see
   "Two table-name flavours" above and the verification notes in the file's header.
5. Click **Save > Save as query**. Fill in:
   - **Name**: the file name without the extension, e.g. `daily-usage`.
   - **Save to**: a **Query pack**. Create one the first time (e.g. `reqnroll-ide-queries` in the environment's resource
     group) and reuse it for every query.
   - **Category / description / tags**: e.g. category `Reqnroll IDE support`, and the one-line description from the table above.
6. Repeat for each file. The saved queries then appear in the workspace's **Queries hub** for anyone with access.
7. To chart a result, run the query and switch to the **Chart** tab, or pin it to an Azure dashboard or workbook.

If you only have access to the Application Insights resource (not the workspace), use **Monitoring > Logs** on the
resource instead and apply the column swaps above. Queries saved there live with that resource.

The longer-term plan under #1190 is to deploy these (plus workbooks and alerts) from this repository with Bicep and a
GitHub Actions workflow using a federated identity scoped to the two IdeSupport resource groups, so the manual steps above
become a fallback.
