import * as vscode from 'vscode';
import { LanguageClient, TelemetryEventNotification } from 'vscode-languageclient/node';
import { TelemetryReporter } from '@vscode/extension-telemetry';
import { createTelemetryDebugLogFromEnvironment } from './logging/telemetryDebugLog';

// Same Application Insights resource VS's AnalyticsTransmitter uses (see
// src/VisualStudio/Reqnroll.IdeSupport.VisualStudio.VSSDKIntegration/Analytics/InstrumentationKey.txt)
// so usage events from every Reqnroll IDE client land in the same place.
const CONNECTION_STRING = 'InstrumentationKey=3fd018ff-819d-4685-a6e1-6f09bc98d20b';

type TelemetryPropertyValue = string | number | boolean | null | undefined;

interface TelemetryEventParams {
  eventName?: string;
  properties?: Record<string, TelemetryPropertyValue>;
}

/**
 * REQNROLL_TELEMETRY_ENABLED is the cross-IDE kill switch (unset or "1" = enabled, anything
 * else = disabled) also honoured by Rider's RiderTelemetryTransmitter and VS's
 * TelemetryTransmitter. VS Code's own `telemetry.telemetryLevel` opt-out is enforced separately
 * by TelemetryReporter itself.
 */
function isTelemetryEnabledByEnv(): boolean {
  const value = process.env.REQNROLL_TELEMETRY_ENABLED;
  return value === undefined || value === '1';
}

// Module-scoped so `sendTelemetryEvent` (client-originated events, e.g. goToHooks.ts) can reuse
// the same reporter instance `registerTelemetry` creates, rather than every call site needing its
// own TelemetryReporter/subscription. Undefined until registerTelemetry runs, and again once its
// subscription disposes -- sendTelemetryEvent silently no-ops in both cases (e.g. in unit tests
// that never call registerTelemetry at all).
let reporter: TelemetryReporter | undefined;

/**
 * Forwards the server's `telemetry/event` notifications (see ILspTelemetryService /
 * LspTelemetryService.cs) to Application Insights, mirroring what VS's
 * TelemetryEventInterceptor.cs does for the Visual Studio client. TelemetryReporter routes
 * through vscode.env's telemetry logger internally, so this automatically honours the user's
 * global telemetry opt-out (`telemetry.telemetryLevel`) in addition to the env-var kill switch
 * checked here.
 */
export function registerTelemetry(client: LanguageClient, context: vscode.ExtensionContext): void {
  if (!isTelemetryEnabledByEnv()) return;

  reporter = new TelemetryReporter(CONNECTION_STRING);
  context.subscriptions.push(reporter);
  context.subscriptions.push({
    dispose: () => {
      reporter = undefined;
    },
  });

  context.subscriptions.push(
    client.onNotification(TelemetryEventNotification.type, (params: unknown) => {
      const { eventName, properties } = (params ?? {}) as TelemetryEventParams;
      if (!eventName) return;

      sendTelemetryEvent(eventName, properties);
    }),
  );
}

/**
 * Stringifies `properties` (dropping null/undefined) and stamps the host's client identity (issue
 * #844), matching what VS's `TelemetryTransmitter` and Rider's `RiderTelemetryTransmitter` stamp:
 * `Ide`/`IdeVersion`/`ExtensionVersion`, plus the canonical `IdeClient` (same vocabulary the LSP
 * server stamps on server-originated events). `IdeClient` is only added when absent so a
 * server-stamped value is never overridden. Exported for tests.
 */
export function withClientIdentity(
  properties?: Record<string, TelemetryPropertyValue>,
): Record<string, string> {
  const stringProps: Record<string, string> = {};
  for (const [key, value] of Object.entries(properties ?? {})) {
    if (value !== undefined && value !== null) stringProps[key] = String(value);
  }
  stringProps.IdeClient ??= 'vscode';
  stringProps.Ide = 'Visual Studio Code';
  stringProps.IdeVersion = vscode.version;
  stringProps.ExtensionVersion = extensionVersion();
  return stringProps;
}

function extensionVersion(): string {
  const packageJson = vscode.extensions.getExtension('reqnroll.reqnroll-ide-support')
    ?.packageJSON as { version?: unknown } | undefined;
  return typeof packageJson?.version === 'string' ? packageJson.version : 'unknown';
}

/**
 * Sends a client-originated telemetry event directly, for the rare case where the event describes
 * something only the client knows (e.g. `doGoToHooks.ts`'s "GoToHook command executed" -- issue
 * #698: the server's own `reqnroll/findHooks` handler cannot tell a genuine navigation apart from
 * the classic VS CodeLens's Details-popup prefetch, so it no longer claims to). Most telemetry
 * should still be server-originated and reach Application Insights via the notification forwarder
 * in `registerTelemetry` above; reach for this only when the client itself is the source of truth.
 * A no-op (other than mirroring to the debug log, see below) before `registerTelemetry` has run,
 * after its subscription disposes, or while telemetry is disabled.
 *
 * Also the single point every client-originated and server-relayed event passes through (the
 * notification forwarder in `registerTelemetry` calls this too), so mirroring the attempt here to
 * the local debug log (issue #799) — independent of `enabled`/whether a reporter exists, matching
 * VS's `TelemetryTransmitter.TransmitEvent` — covers both paths with one call site.
 */
export function sendTelemetryEvent(
  eventName: string,
  properties?: Record<string, TelemetryPropertyValue>,
): void {
  const enabled = isTelemetryEnabledByEnv();
  const debugLog = createTelemetryDebugLogFromEnvironment();

  if (!reporter) {
    debugLog.record('host', eventName, properties, enabled, false);
    return;
  }

  const stringProps = withClientIdentity(properties);

  reporter.sendTelemetryEvent(eventName, stringProps);
  debugLog.record('host', eventName, properties, enabled, true);
}
