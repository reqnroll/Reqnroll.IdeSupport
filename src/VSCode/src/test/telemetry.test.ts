import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';
import { TelemetryReporter } from '@vscode/extension-telemetry';
import {
  ensureTelemetryReporter,
  registerTelemetry,
  resetTelemetryReporterForTests,
  sendTelemetryEvent,
  withClientIdentity,
} from '../telemetry';

function fakeClient(): { client: LanguageClient; fire: (params: unknown) => void } {
  let handler: ((params: unknown) => void) | undefined;
  const client = {
    onNotification: (_type: unknown, listener: (params: unknown) => void) => {
      handler = listener;
      return { dispose: () => undefined };
    },
  } as unknown as LanguageClient;
  return { client, fire: (params: unknown) => handler?.(params) };
}

function fakeContext(): vscode.ExtensionContext {
  return { subscriptions: [] } as unknown as vscode.ExtensionContext;
}

interface RecordedEvent {
  eventName: string;
  properties?: Record<string, string>;
}

/**
 * Stubs `TelemetryReporter.prototype.sendTelemetryEvent` for the duration of `fn`, so
 * `registerTelemetry`'s forwarding logic can be verified without ever reaching the real
 * Application Insights / 1DS sending pipeline underneath it (which `sendTelemetryEvent` is the
 * sole gateway into -- see `internalSendTelemetryEvent` in
 * `@vscode/extension-telemetry`'s `BaseTelemetryReporter`). Disposes `context.subscriptions`
 * afterwards, since `registerTelemetry` pushes both the reporter and the notification-listener
 * disposable onto it.
 */
async function withStubbedSendTelemetryEvent(
  context: vscode.ExtensionContext,
  fn: (calls: RecordedEvent[]) => void | Promise<void>,
): Promise<void> {
  const calls: RecordedEvent[] = [];
  const proto = TelemetryReporter.prototype as unknown as {
    sendTelemetryEvent: (eventName: string, properties?: Record<string, string>) => void;
  };
  const original = proto.sendTelemetryEvent;
  proto.sendTelemetryEvent = (eventName: string, properties?: Record<string, string>) => {
    calls.push({ eventName, properties });
  };
  try {
    await fn(calls);
  } finally {
    proto.sendTelemetryEvent = original;
    for (const sub of context.subscriptions) sub.dispose();
  }
}

const IDENTITY_KEYS = ['IdeClient', 'Ide', 'IdeVersion', 'ExtensionVersion'];

function withoutIdentity(properties?: Record<string, string>): Record<string, string> {
  return Object.fromEntries(
    Object.entries(properties ?? {}).filter(([k]) => !IDENTITY_KEYS.includes(k)),
  );
}

suite('telemetry', () => {
  // The activated extension owns a module-level reporter (ensureTelemetryReporter, #845); start clean.
  setup(() => resetTelemetryReporterForTests());

  suite('client identity (#844)', () => {
    test('withClientIdentity stamps IdeClient, Ide, IdeVersion and ExtensionVersion', () => {
      const props = withClientIdentity({ a: 1 });

      assert.strictEqual(props.a, '1');
      assert.strictEqual(props.IdeClient, 'vscode');
      assert.strictEqual(props.Ide, 'Visual Studio Code');
      assert.strictEqual(props.IdeVersion, vscode.version);
      assert.ok(props.ExtensionVersion.length > 0);
    });

    test('withClientIdentity does not override a server-stamped IdeClient', () => {
      assert.strictEqual(withClientIdentity({ IdeClient: 'explicit' }).IdeClient, 'explicit');
    });

    test('sendTelemetryEvent stamps identity on host-originated events', async () => {
      const context = fakeContext();
      const { client } = fakeClient();

      await withStubbedSendTelemetryEvent(context, (calls) => {
        registerTelemetry(client, context);
        sendTelemetryEvent('GoToHook command executed');

        assert.strictEqual(calls[0].properties?.IdeClient, 'vscode');
        assert.strictEqual(calls[0].properties?.Ide, 'Visual Studio Code');
      });
    });
  });

  suite('ensureTelemetryReporter (#845)', () => {
    test('lets client-originated events be sent before registerTelemetry has run, and registerTelemetry reuses the reporter', async () => {
      const context = fakeContext();
      const { client } = fakeClient();

      await withStubbedSendTelemetryEvent(context, (calls) => {
        ensureTelemetryReporter(context);
        sendTelemetryEvent('ServerStartFailed', { Reason: 'StartFailed', AttemptNumber: 1 });
        registerTelemetry(client, context);
        ensureTelemetryReporter(context);

        assert.strictEqual(calls.length, 1);
        assert.strictEqual(calls[0].eventName, 'ServerStartFailed');
        assert.deepStrictEqual(withoutIdentity(calls[0].properties), {
          Reason: 'StartFailed',
          AttemptNumber: '1',
        });
        // One reporter, one disposal hook: nothing was registered twice.
        assert.strictEqual(context.subscriptions.length, 3);
      });
    });
  });

  suite('registerTelemetry', () => {
    const originalEnv = process.env.REQNROLL_TELEMETRY_ENABLED;

    teardown(() => {
      if (originalEnv === undefined) delete process.env.REQNROLL_TELEMETRY_ENABLED;
      else process.env.REQNROLL_TELEMETRY_ENABLED = originalEnv;
    });

    test('does not forward events when REQNROLL_TELEMETRY_ENABLED is "0"', async () => {
      process.env.REQNROLL_TELEMETRY_ENABLED = '0';
      const { client, fire } = fakeClient();
      const context = fakeContext();

      await withStubbedSendTelemetryEvent(context, (calls) => {
        registerTelemetry(client, context);
        fire({ eventName: 'reqnroll/stepDefined' });

        assert.strictEqual(calls.length, 0);
      });
    });

    test('forwards events when REQNROLL_TELEMETRY_ENABLED is "1"', async () => {
      process.env.REQNROLL_TELEMETRY_ENABLED = '1';
      const { client, fire } = fakeClient();
      const context = fakeContext();

      await withStubbedSendTelemetryEvent(context, (calls) => {
        registerTelemetry(client, context);
        fire({ eventName: 'reqnroll/stepDefined' });

        assert.strictEqual(calls.length, 1);
      });
    });

    test('forwards events when REQNROLL_TELEMETRY_ENABLED is unset', async () => {
      delete process.env.REQNROLL_TELEMETRY_ENABLED;
      const { client, fire } = fakeClient();
      const context = fakeContext();

      await withStubbedSendTelemetryEvent(context, (calls) => {
        registerTelemetry(client, context);
        fire({ eventName: 'reqnroll/stepDefined' });

        assert.strictEqual(calls.length, 1);
      });
    });

    test('forwards a telemetry/event notification to the reporter with stringified properties', async () => {
      const { client, fire } = fakeClient();
      const context = fakeContext();

      await withStubbedSendTelemetryEvent(context, (calls) => {
        registerTelemetry(client, context);
        fire({ eventName: 'reqnroll/stepDefined', properties: { count: 3, ok: true } });

        assert.strictEqual(calls.length, 1);
        assert.strictEqual(calls[0].eventName, 'reqnroll/stepDefined');
        assert.deepStrictEqual(withoutIdentity(calls[0].properties), { count: '3', ok: 'true' });
      });
    });

    test('forwards FeatureUsageSummary counts (#582) as the exact JSON string the server sent', async () => {
      const { client, fire } = fakeClient();
      const context = fakeContext();
      const lookupCounts = '{"CodeAction":1,"Completion.Step":14}';

      await withStubbedSendTelemetryEvent(context, (calls) => {
        registerTelemetry(client, context);
        fire({
          eventName: 'FeatureUsageSummary',
          properties: { LookupCounts: lookupCounts, Sequence: 2 },
        });

        assert.strictEqual(calls.length, 1);
        assert.strictEqual(calls[0].properties?.LookupCounts, lookupCounts);
        assert.strictEqual(calls[0].properties?.Sequence, '2');
      });
    });

    test('ignores a notification with no eventName', async () => {
      const { client, fire } = fakeClient();
      const context = fakeContext();

      await withStubbedSendTelemetryEvent(context, (calls) => {
        registerTelemetry(client, context);
        fire({ properties: { a: 1 } });

        assert.strictEqual(calls.length, 0);
      });
    });

    test('ignores a notification with no params at all', async () => {
      const { client, fire } = fakeClient();
      const context = fakeContext();

      await withStubbedSendTelemetryEvent(context, (calls) => {
        registerTelemetry(client, context);
        fire(undefined);

        assert.strictEqual(calls.length, 0);
      });
    });

    test('sends an event with no properties as an identity-only object', async () => {
      const { client, fire } = fakeClient();
      const context = fakeContext();

      await withStubbedSendTelemetryEvent(context, (calls) => {
        registerTelemetry(client, context);
        fire({ eventName: 'reqnroll/noProps' });

        assert.strictEqual(calls.length, 1);
        assert.deepStrictEqual(withoutIdentity(calls[0].properties), {});
      });
    });

    test('drops null/undefined property values rather than stringifying them', async () => {
      const { client, fire } = fakeClient();
      const context = fakeContext();

      await withStubbedSendTelemetryEvent(context, (calls) => {
        registerTelemetry(client, context);
        fire({
          eventName: 'reqnroll/x',
          properties: { present: 'yes', missing: undefined, absent: null },
        });

        assert.deepStrictEqual(withoutIdentity(calls[0].properties), { present: 'yes' });
      });
    });
  });

  // sendTelemetryEvent is the single point every client-originated and server-relayed event
  // passes through, so its REQNROLL_TELEMETRY_DEBUG_LOG mirror (issue #799) is exercised directly
  // here rather than through registerTelemetry's notification forwarder.
  suite('sendTelemetryEvent debug log mirror', () => {
    const originalDebugLogEnv = process.env.REQNROLL_TELEMETRY_DEBUG_LOG;
    const originalEnabledEnv = process.env.REQNROLL_TELEMETRY_ENABLED;
    let target: string;

    setup(() => {
      target = path.join(os.tmpdir(), `reqnroll-tel-test-${Date.now()}-${Math.random()}.jsonl`);
      process.env.REQNROLL_TELEMETRY_DEBUG_LOG = target;
    });

    teardown(() => {
      if (originalDebugLogEnv === undefined) delete process.env.REQNROLL_TELEMETRY_DEBUG_LOG;
      else process.env.REQNROLL_TELEMETRY_DEBUG_LOG = originalDebugLogEnv;
      if (originalEnabledEnv === undefined) delete process.env.REQNROLL_TELEMETRY_ENABLED;
      else process.env.REQNROLL_TELEMETRY_ENABLED = originalEnabledEnv;
      if (fs.existsSync(target)) fs.unlinkSync(target);
    });

    interface DebugLogRecord {
      source: string;
      event: string;
      props?: Record<string, unknown>;
      enabled?: boolean;
      transmitted?: boolean;
    }

    function readRecords(): DebugLogRecord[] {
      return fs
        .readFileSync(target, 'utf8')
        .trim()
        .split('\n')
        .map((line) => JSON.parse(line) as DebugLogRecord);
    }

    test('mirrors as transmitted:false when no reporter has been registered, even though the kill switch defaults to enabled', () => {
      sendTelemetryEvent('reqnroll/noReporterYet', { a: 1 });

      const records = readRecords();
      assert.strictEqual(records.length, 1);
      assert.strictEqual(records[0].source, 'host');
      assert.strictEqual(records[0].event, 'reqnroll/noReporterYet');
      assert.strictEqual(records[0].enabled, true);
      assert.strictEqual(records[0].transmitted, false);
    });

    test('mirrors as enabled:true/transmitted:true once a reporter is registered and the event is sent', async () => {
      process.env.REQNROLL_TELEMETRY_ENABLED = '1';
      const { client } = fakeClient();
      const context = fakeContext();

      await withStubbedSendTelemetryEvent(context, (calls) => {
        registerTelemetry(client, context);
        sendTelemetryEvent('reqnroll/withReporter', { count: 3 });

        assert.strictEqual(calls.length, 1);
        const records = readRecords();
        assert.strictEqual(records.length, 1);
        assert.strictEqual(records[0].enabled, true);
        assert.strictEqual(records[0].transmitted, true);
        assert.strictEqual(records[0].props?.count, 3);
      });
    });

    test('mirrors as enabled:false/transmitted:false when the kill switch is off, even though it still no-ops the reporter', () => {
      process.env.REQNROLL_TELEMETRY_ENABLED = '0';

      sendTelemetryEvent('reqnroll/killSwitchOff');

      const records = readRecords();
      assert.strictEqual(records.length, 1);
      assert.strictEqual(records[0].enabled, false);
      assert.strictEqual(records[0].transmitted, false);
    });
  });
});
