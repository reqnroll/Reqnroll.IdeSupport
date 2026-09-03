import * as assert from 'assert';
import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';
import { TelemetryReporter } from '@vscode/extension-telemetry';
import { registerTelemetry } from '../telemetry';

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

suite('telemetry', () => {
  suite('registerTelemetry', () => {
    test('forwards a telemetry/event notification to the reporter with stringified properties', async () => {
      const { client, fire } = fakeClient();
      const context = fakeContext();

      await withStubbedSendTelemetryEvent(context, (calls) => {
        registerTelemetry(client, context);
        fire({ eventName: 'reqnroll/stepDefined', properties: { count: 3, ok: true } });

        assert.strictEqual(calls.length, 1);
        assert.strictEqual(calls[0].eventName, 'reqnroll/stepDefined');
        assert.deepStrictEqual(calls[0].properties, { count: '3', ok: 'true' });
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

    test('sends an event with no properties as an empty object', async () => {
      const { client, fire } = fakeClient();
      const context = fakeContext();

      await withStubbedSendTelemetryEvent(context, (calls) => {
        registerTelemetry(client, context);
        fire({ eventName: 'reqnroll/noProps' });

        assert.strictEqual(calls.length, 1);
        assert.deepStrictEqual(calls[0].properties, {});
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

        assert.deepStrictEqual(calls[0].properties, { present: 'yes' });
      });
    });
  });
});
