// Built-in Application Insights connection strings (issue #1160). One resource per environment;
// the build picks one via REQNROLL_TELEMETRY_ENV (see scripts/generate-telemetry-environment.mjs):
// 'prod' for release builds, 'dev' (the default) for everything else. The other IDE clients carry
// the same pair:
//   - VS:    src/VisualStudio/Reqnroll.IdeSupport.VisualStudio.VSSDKIntegration/Telemetry/InstrumentationKey.{Dev,Prod}.txt
//   - Rider: src/Rider/src/main/kotlin/net/reqnroll/idesupport/rider/telemetry/TelemetryConnections.kt
// Connection strings are not secrets (they ship inside every client), so they are committed.
//
// TODO(#1160): paste the real connection strings (reqnroll-ide-appinsights-dev / -prod).
export const DEV_CONNECTION_STRING =
  'InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://TODO-region.in.applicationinsights.azure.com/;LiveEndpoint=https://TODO-region.livediagnostics.monitor.azure.com/;ApplicationId=00000000-0000-0000-0000-000000000000';
export const PROD_CONNECTION_STRING =
  'InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://TODO-region.in.applicationinsights.azure.com/;LiveEndpoint=https://TODO-region.livediagnostics.monitor.azure.com/;ApplicationId=00000000-0000-0000-0000-000000000000';

export type TelemetryEnvironment = 'dev' | 'prod';

export function builtInConnectionString(environment: TelemetryEnvironment): string {
  return environment === 'prod' ? PROD_CONNECTION_STRING : DEV_CONNECTION_STRING;
}
