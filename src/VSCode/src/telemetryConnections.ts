// Built-in Application Insights connection strings (issue #1160). One resource per environment;
// the build picks one via REQNROLL_TELEMETRY_ENV (see scripts/generate-telemetry-environment.mjs):
// 'prod' for release builds, 'dev' (the default) for everything else. The other IDE clients carry
// the same pair:
//   - VS:    src/VisualStudio/Reqnroll.IdeSupport.VisualStudio.VSSDKIntegration/Telemetry/InstrumentationKey.{Dev,Prod}.txt
//   - Rider: src/Rider/src/main/kotlin/net/reqnroll/idesupport/rider/telemetry/TelemetryConnections.kt
// Connection strings are not secrets (they ship inside every client), so they are committed.
export const DEV_CONNECTION_STRING =
  'InstrumentationKey=88a37add-31e4-4fff-bb4d-fc6ea8278559;IngestionEndpoint=https://westus3-1.in.applicationinsights.azure.com/;LiveEndpoint=https://westus3.livediagnostics.monitor.azure.com/;ApplicationId=d7821db2-7d0f-425d-b29f-81c4045e856d';
export const PROD_CONNECTION_STRING =
  'InstrumentationKey=515d6796-e169-41ba-81e1-a52564258c25;IngestionEndpoint=https://eastus-8.in.applicationinsights.azure.com/;LiveEndpoint=https://eastus.livediagnostics.monitor.azure.com/;ApplicationId=e579b405-7f07-4af3-af5a-91d12a6d2ec0';

export type TelemetryEnvironment = 'dev' | 'prod';

export function builtInConnectionString(environment: TelemetryEnvironment): string {
  return environment === 'prod' ? PROD_CONNECTION_STRING : DEV_CONNECTION_STRING;
}
