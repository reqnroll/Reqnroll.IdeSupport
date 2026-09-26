import * as vscode from 'vscode';
import { LanguageClient, TelemetryEventNotification } from 'vscode-languageclient/node';
import { TelemetryReporter } from '@vscode/extension-telemetry';

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

  const reporter = new TelemetryReporter(CONNECTION_STRING);
  context.subscriptions.push(reporter);

  context.subscriptions.push(
    client.onNotification(TelemetryEventNotification.type, (params: unknown) => {
      const { eventName, properties } = (params ?? {}) as TelemetryEventParams;
      if (!eventName) return;

      const stringProps: Record<string, string> = {};
      for (const [key, value] of Object.entries(properties ?? {})) {
        if (value !== undefined && value !== null) stringProps[key] = String(value);
      }

      reporter.sendTelemetryEvent(eventName, stringProps);
    }),
  );
}
