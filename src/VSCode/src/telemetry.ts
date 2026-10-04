import * as vscode from 'vscode';
import { LanguageClient, TelemetryEventNotification } from 'vscode-languageclient/node';
import { TelemetryReporter } from '@vscode/extension-telemetry';
import { createTelemetryDebugLogFromEnvironment } from './logging/telemetryDebugLog';
import { logInfo } from './logging/appNotify';
import { TelemetryCircuitBreaker } from './telemetryCircuitBreaker';

// Same Application Insights resource VS's AnalyticsTransmitter uses (see
// src/VisualStudio/Reqnroll.IdeSupport.VisualStudio.VSSDKIntegration/Telemetry/InstrumentationKey.txt)
// so usage events from every Reqnroll IDE client land in the same place.
const CONNECTION_STRING = 'InstrumentationKey=3fd018ff-819d-4685-a6e1-6f09bc98d20b';

/**
 * Developer override (issue #889): REQNROLL_DEBUG_TELEMETRY_CONNECTION_STRING, shared with VS and Rider,
 * replaces the built-in connection string when it carries a non-empty `InstrumentationKey`.
 * Anything else is ignored (`onInvalid` is told why) so a typo never silently drops events.
 */
export function resolveConnectionString(
  override: string | undefined,
  onInvalid?: (message: string) => void,
): string {
  const value = override?.trim();
  if (!value) return CONNECTION_STRING;
  if (!hasInstrumentationKey(value)) {
    onInvalid?.(
      'REQNROLL_DEBUG_TELEMETRY_CONNECTION_STRING has no InstrumentationKey; ignoring it.',
    );
    return CONNECTION_STRING;
  }
  return value;
}

function hasInstrumentationKey(value: string): boolean {
  return value.split(';').some((part) => /^\s*InstrumentationKey\s*=\s*\S/i.test(part));
}

/**
 * Debug-build guard (issue #889): an extension running from source (F5, `ExtensionMode.Development`)
 * must not send to the built-in production resource, so without a usable override it sends nothing.
 */
export function isBuiltInConnectionBlocked(
  extensionMode: vscode.ExtensionMode,
  override: string | undefined,
): boolean {
  return (
    extensionMode === vscode.ExtensionMode.Development &&
    !hasInstrumentationKey(override?.trim() ?? '')
  );
}

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

// Issue #845: the server emits its final events (`FeatureUsageSummary`, `ServerSessionEnded`) while
// handling the LSP `shutdown` request, i.e. during `client.stop()`. VS Code disposes
// `context.subscriptions` as soon as `deactivate()` has been *called* (it does not wait for the returned
// promise), which would detach the notification forwarder and the reporter before those events arrive.
// While a gate is set (see `deferTelemetryTeardownUntil`) their disposal therefore waits for it.
let teardownGate: Promise<void> | undefined;
const deferredTeardowns: Promise<void>[] = [];

/**
 * Makes the forwarder / reporter disposals wait until `gate` (the client's `stop()`) settles, so the
 * server's shutdown-time telemetry is still relayed. Call before `deactivate()` returns, and return
 * `drainTelemetryTeardown()` after the gate so the host stays alive for the final flush.
 */
export function deferTelemetryTeardownUntil(gate: Thenable<unknown>): void {
  teardownGate = Promise.resolve(gate).then(
    () => undefined,
    () => undefined,
  );
}

/** Resolves once every disposal deferred by `deferTelemetryTeardownUntil` has run; clears the gate. */
export function drainTelemetryTeardown(): Thenable<void> {
  return Promise.all(deferredTeardowns.splice(0)).then(() => {
    teardownGate = undefined;
  });
}

/** Runs `action` now, or after the teardown gate when one is set. */
function afterTeardownGate(action: () => void | Thenable<void>): Thenable<void> {
  if (!teardownGate) return Promise.resolve(action());
  const deferred = teardownGate.then(action);
  deferredTeardowns.push(deferred);
  return deferred;
}

/** Upper bound on the flush performed when the reporter is disposed (extension deactivate). */
const DISPOSE_TIMEOUT_MS = 500;

// One breaker per session (re-created by registerTelemetry): once the endpoint has failed, every
// later event is dropped without touching the reporter, and the user sees a single notice (#859).
let breaker = newBreaker();

function newBreaker(): TelemetryCircuitBreaker {
  return new TelemetryCircuitBreaker(logInfo, logInfo);
}

/**
 * Disposes `r` without ever delaying deactivation: skipped entirely once the breaker is open (a
 * flush cannot succeed), otherwise raced against a short timeout, and never throws or rejects.
 */
function disposeReporterBounded(r: TelemetryReporter): Thenable<void> {
  if (breaker.isOpen) {
    void Promise.resolve()
      .then(() => r.dispose())
      .catch(() => undefined);
    return Promise.resolve();
  }
  let timer: NodeJS.Timeout | undefined;
  const bound = new Promise<void>((resolve) => {
    timer = setTimeout(resolve, DISPOSE_TIMEOUT_MS);
  });
  const flush = Promise.resolve()
    .then(() => r.dispose())
    .then(
      () => undefined,
      (err: unknown) => breaker.recordFailure(String(err)),
    );
  return Promise.race([flush, bound]).finally(() => timer && clearTimeout(timer));
}

/**
 * Creates the shared `TelemetryReporter` if telemetry is enabled and it does not exist yet. Called by
 * `extension.ts` *before* `client.start()` so that client-originated server-lifecycle events
 * (`ServerStartFailed`, issue #845) can be sent when the server never comes up, and again (as a
 * no-op) by `registerTelemetry` once it has.
 */
export function ensureTelemetryReporter(context: vscode.ExtensionContext): void {
  if (reporter || !isTelemetryEnabledByEnv()) return;

  const connectionOverride = process.env.REQNROLL_DEBUG_TELEMETRY_CONNECTION_STRING;
  if (isBuiltInConnectionBlocked(context.extensionMode, connectionOverride)) {
    logInfo(
      'Telemetry is not sent from a development build unless REQNROLL_DEBUG_TELEMETRY_CONNECTION_STRING is set.',
    );
    return;
  }

  breaker = newBreaker();
  const created = new TelemetryReporter(resolveConnectionString(connectionOverride, logInfo));
  reporter = created;
  context.subscriptions.push({
    dispose: () => afterTeardownGate(() => disposeReporterBounded(created)),
  });
  context.subscriptions.push({
    dispose: () =>
      afterTeardownGate(() => {
        reporter = undefined;
      }),
  });
}

/**
 * Test hook: forgets the module-level reporter. The extension itself creates one at activation
 * (`ensureTelemetryReporter`), which the extension-host tests share a module instance with, so tests
 * that assert on "no reporter registered" start from a clean slate. Not used in production code.
 */
export function resetTelemetryReporterForTests(): void {
  reporter = undefined;
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
  ensureTelemetryReporter(context);
  if (!reporter) return;

  const listener = client.onNotification(TelemetryEventNotification.type, (params: unknown) => {
    const { eventName, properties } = (params ?? {}) as TelemetryEventParams;
    if (!eventName) return;

    sendTelemetryEvent(eventName, properties);
  });
  context.subscriptions.push({
    dispose: () =>
      afterTeardownGate(() => {
        listener.dispose();
      }),
  });
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

  if (breaker.isOpen) {
    debugLog.record(
      'host',
      eventName,
      properties,
      enabled,
      false,
      'telemetry endpoint unreachable',
    );
    return;
  }

  try {
    reporter.sendTelemetryEvent(eventName, stringProps);
  } catch (err) {
    // Telemetry must never surface to the user (#859): record it, open the breaker, carry on.
    const message = err instanceof Error ? err.message : String(err);
    breaker.recordFailure(message);
    debugLog.record('host', eventName, properties, enabled, false, message);
    return;
  }
  debugLog.record('host', eventName, properties, enabled, true);
}
