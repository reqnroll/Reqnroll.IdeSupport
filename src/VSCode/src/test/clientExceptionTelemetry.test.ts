import * as assert from 'assert';
import * as vscode from 'vscode';
import { ResponseError, type ErrorHandler } from 'vscode-languageclient';
import { TelemetryReporter } from '@vscode/extension-telemetry';
import {
  ClientExceptionReporter,
  MAX_EVENTS_PER_SESSION,
  SOURCE_LANGUAGE_CLIENT_ERROR_HANDLER,
  buildExceptionProperties,
  createReportingErrorHandler,
  guardCommand,
  executeForeignCommand,
  reportClientException,
  scrubMessage,
  wrapErrorHandler,
} from '../clientExceptionTelemetry';
import { ensureTelemetryReporter, resetTelemetryReporterForTests } from '../telemetry';

interface Sent {
  name: string;
  props: Record<string, string>;
}

function recordingReporter(): { reporter: ClientExceptionReporter; sent: Sent[] } {
  const sent: Sent[] = [];
  return {
    reporter: new ClientExceptionReporter((name, props) => sent.push({ name, props })),
    sent,
  };
}

suite('clientExceptionTelemetry (#621)', () => {
  suite('payload', () => {
    test('uses the server UnhandledException schema plus ExceptionOrigin=Client and a closed Source', () => {
      const props = buildExceptionProperties('Activation', new TypeError('boom'));

      assert.deepStrictEqual(props, {
        ExceptionType: 'TypeError',
        Message: 'boom',
        Source: 'Activation',
        ExceptionOrigin: 'Client',
      });
    });

    test('never carries a stack trace', () => {
      const error = new Error('x');
      const props = buildExceptionProperties('Activation', error);

      for (const value of Object.values(props)) {
        assert.ok(!value.includes('at '), `unexpected stack-like text: ${value}`);
      }
      assert.deepStrictEqual(Object.keys(props).sort(), [
        'ExceptionOrigin',
        'ExceptionType',
        'Message',
        'Source',
      ]);
    });

    test('redacts Windows, UNC and POSIX paths from the message', () => {
      const message = scrubMessage(
        'ENOENT C:\\Users\\bob\\proj\\a.feature and \\\\srv\\share\\x and /home/bob/proj/b.cs failed',
      );

      assert.strictEqual(message, 'ENOENT <path> and <path> and <path> failed');
    });

    test('redacts a drive-letter path whose user directory contains spaces', () => {
      const message = scrubMessage(
        'EACCES C:\\Users\\John Smith\\My Projects\\Calc\\a.feature denied',
      );

      assert.ok(!message.includes('John'), message);
      assert.ok(!message.includes('Smith'), message);
      assert.ok(!message.includes('Projects'), message);
      assert.strictEqual(message, 'EACCES <path> denied');
    });

    test('redacts a UNC path with spaces and a POSIX path with spaces', () => {
      assert.strictEqual(scrubMessage('\\\\srv\\my share\\dir\\f failed'), '<path> failed');
      assert.strictEqual(scrubMessage('open /home/john smith/work/x now'), 'open <path> now');
    });

    test('redacts a quoted path wholly, spaces included', () => {
      const message = scrubMessage(
        'Cannot find "C:\\Users\\Jane Doe\\proj\\x.csproj" or ' + "'/home/jane doe/x'",
      );

      assert.ok(!message.includes('Jane'), message);
      assert.ok(!message.includes('proj'), message);
    });

    test('drops a URL wholly, so its query string token and fragment never leave', () => {
      const message = scrubMessage(
        'fetch https://example.com/a/b?token=SECRET123&x=1#frag failed; file:///c:/Users/bob/x.json too',
      );

      assert.ok(!message.includes('SECRET123'), message);
      assert.ok(!message.includes('frag'), message);
      assert.ok(!message.includes('bob'), message);
      assert.strictEqual(message, 'fetch <url> failed; <url> too');
    });

    test('redacts relative paths and ./ ../ forms', () => {
      const message = scrubMessage(
        'missing src/Features/Calc.feature and ../shared/x and .\\obj\\y',
      );

      assert.ok(!message.includes('Calc'), message);
      assert.ok(!message.includes('shared'), message);
      assert.ok(!message.includes('obj'), message);
    });

    test('redacts bare file names with source or config extensions', () => {
      const message = scrubMessage(
        'Error in Calculator.feature, Steps.cs, App.csproj, appsettings.json and README.md',
      );

      assert.strictEqual(message, 'Error in <path>, <path>, <path>, <path> and <path>');
    });

    test('redacts %VAR%, $VAR and ~ home-style paths', () => {
      assert.strictEqual(scrubMessage('x %USERPROFILE%\\proj\\a here'), 'x <path> here');
      assert.strictEqual(scrubMessage('x ~/work/proj/a here'), 'x <path> here');
      assert.strictEqual(scrubMessage('x $HOME/work/a here'), 'x <path> here');
    });

    test('leaves ordinary words, LSP-method-looking words and dotted type names alone', () => {
      assert.strictEqual(
        scrubMessage('and/or textDocument/definition System.Text.Json x'),
        'and/or textDocument/definition System.Text.Json x',
      );
    });

    test('drops the quoted source snippet of a JSON.parse-style message', () => {
      let thrown: Error | undefined;
      try {
        JSON.parse('{"stepText": Given my secret password is hunter2}');
      } catch (e) {
        thrown = e as Error;
      }
      const props = buildExceptionProperties('Command:reqnroll.x', thrown);

      assert.ok(props.Message.includes('<text>'), props.Message);
      assert.ok(!props.Message.includes('hunter2'), props.Message);
      assert.ok(!props.Message.includes('stepText'), props.Message);
      // Nested quotes inside the snippet must not end the dropped span early.
      assert.strictEqual(
        scrubMessage('bad "{"stepText": "Given my hunter2", }" in JSON'),
        'bad <text> in JSON',
      );
      assert.ok(!props.Message.includes('secret'), props.Message);
      assert.strictEqual(
        scrubMessage('Unexpected token } , "{a: secret}" is not valid JSON'),
        'Unexpected token } , <text> is not valid JSON',
      );
    });

    test('scrubs paths and caps the transmitted message at 128 characters', () => {
      const props = buildExceptionProperties(
        'Activation',
        new Error(`cannot open /home/bob/secret.feature ${'x'.repeat(2000)}`),
      );

      assert.ok(!props.Message.includes('bob'));
      assert.ok(props.Message.startsWith('cannot open <path>'));
      assert.ok(props.Message.length <= 128);
    });

    test('a custom error class reports its name; an unsafe name falls back to Error', () => {
      class FooError extends Error {}
      const odd = new Error('x');
      odd.name = 'weird name with spaces / C:\\path';

      assert.strictEqual(
        buildExceptionProperties('s', new FooError('x')).ExceptionType,
        'FooError',
      );
      assert.strictEqual(buildExceptionProperties('s', odd).ExceptionType, 'Error');
    });

    test('a thrown non-Error value is reported as NonError with its scrubbed text', () => {
      const props = buildExceptionProperties('s', 'failed at /home/bob/x');

      assert.strictEqual(props.ExceptionType, 'NonError');
      assert.strictEqual(props.Message, 'failed at <path>');
    });
  });

  suite('rate limiting', () => {
    test('sends an identical exception from the same source only once', () => {
      const { reporter, sent } = recordingReporter();

      assert.strictEqual(reporter.report('Activation', new Error('same')), true);
      assert.strictEqual(reporter.report('Activation', new Error('same')), false);

      assert.strictEqual(sent.length, 1);
      assert.strictEqual(sent[0].name, 'UnhandledException');
    });

    test('treats a different source or message as distinct', () => {
      const { reporter, sent } = recordingReporter();

      reporter.report('Activation', new Error('a'));
      reporter.report('Command:reqnroll.goToHooks', new Error('a'));
      reporter.report('Activation', new Error('b'));

      assert.strictEqual(sent.length, 3);
    });

    test('caps the total events per session', () => {
      const { reporter, sent } = recordingReporter();

      for (let i = 0; i < MAX_EVENTS_PER_SESSION + 10; i++) {
        reporter.report('Activation', new Error(`unique ${i}`));
      }

      assert.strictEqual(sent.length, MAX_EVENTS_PER_SESSION);
    });

    test('does not report LSP ResponseError (the server already reports those; text can embed step text)', () => {
      const { reporter, sent } = recordingReporter();

      assert.strictEqual(
        reporter.report(
          'Command:x',
          new ResponseError(-32603, 'No step matches "Given my secret"'),
        ),
        false,
      );
      const wrapped = new Error('request failed', { cause: new ResponseError(-32603, 'inner') });
      assert.strictEqual(reporter.report('Command:x', wrapped), false);
      assert.strictEqual(sent.length, 0);
    });

    test('counts duplicates and cap drops and attaches SuppressedCount to the next sent event', () => {
      const { reporter, sent } = recordingReporter();

      reporter.report('Activation', new Error('a'));
      reporter.report('Activation', new Error('a'));
      reporter.report('Activation', new Error('a'));
      reporter.report('Activation', new Error('b'));
      reporter.report('Activation', new Error('c'));

      assert.strictEqual(sent[0].props.SuppressedCount, undefined);
      assert.strictEqual(sent[1].props.SuppressedCount, '2');
      assert.strictEqual(sent[2].props.SuppressedCount, undefined);
    });

    test('does not report cancellations', () => {
      const { reporter, sent } = recordingReporter();
      const cancelled = new Error('Canceled');
      cancelled.name = 'Canceled';

      assert.strictEqual(reporter.report('Command:x', cancelled), false);
      assert.strictEqual(sent.length, 0);
    });

    test('never throws even when the sender throws', () => {
      const reporter = new ClientExceptionReporter(() => {
        throw new Error('transport down');
      });

      assert.doesNotThrow(() => reporter.report('Activation', new Error('x')));
    });
  });

  suite('guardCommand', () => {
    test('reports a synchronous throw with the command source and rethrows the same error', () => {
      const calls: Array<[string, unknown]> = [];
      const boom = new Error('sync');
      const guarded = guardCommand(
        'reqnroll.toggleComment',
        () => {
          throw boom;
        },
        (source, err) => calls.push([source, err]),
      );

      assert.throws(
        () => guarded(),
        (err) => err === boom,
      );
      assert.deepStrictEqual(calls, [['Command:reqnroll.toggleComment', boom]]);
    });

    test('reports a rejected promise and rethrows the same error', async () => {
      const calls: Array<[string, unknown]> = [];
      const boom = new Error('async');
      const guarded = guardCommand(
        'reqnroll.findStepUsages',
        (_a: string) => Promise.reject(boom),
        (source, err) => calls.push([source, err]),
      );

      await assert.rejects(guarded('x'), (err) => err === boom);
      assert.deepStrictEqual(calls, [['Command:reqnroll.findStepUsages', boom]]);
    });

    test('passes arguments and the result through untouched and reports nothing on success', async () => {
      const calls: unknown[] = [];
      const guarded = guardCommand(
        'reqnroll.goToHooks',
        (a: number, b: number) => Promise.resolve(a + b),
        (...args) => calls.push(args),
      );

      assert.strictEqual(await guarded(2, 3), 5);
      assert.strictEqual(calls.length, 0);
    });
  });

  suite('foreign (delegated built-in) commands', () => {
    test('an error from the F2 fall-through to editor.action.rename is rethrown but never reported', async () => {
      const { reporter, sent } = recordingReporter();
      const boom = new Error('No result.');
      const guarded = guardCommand(
        'reqnroll.renameStepOrSymbol',
        async () => {
          await executeForeignCommand('editor.action.rename', () => Promise.reject(boom));
        },
        (source, err) => void reporter.report(source, err),
      );

      await assert.rejects(guarded(), (err) => err === boom);
      assert.strictEqual(sent.length, 0);
    });

    test('an error from our own code in the same command is still reported', async () => {
      const { reporter, sent } = recordingReporter();
      const guarded = guardCommand(
        'reqnroll.renameStepOrSymbol',
        () => Promise.reject(new Error('ours')),
        (source, err) => void reporter.report(source, err),
      );

      await assert.rejects(guarded());
      assert.strictEqual(sent.length, 1);
    });
  });

  suite('language client error handler', () => {
    function inner(): ErrorHandler & { errors: number; closedCalls: number } {
      const handler = {
        errors: 0,
        closedCalls: 0,
        error() {
          handler.errors++;
          return { action: 1 };
        },
        closed() {
          handler.closedCalls++;
          return { action: 1 };
        },
      };
      return handler;
    }

    test('reports transport errors under the closed handler source and delegates the decision', async () => {
      const calls: Array<[string, unknown]> = [];
      const delegate = inner();
      const wrapped = wrapErrorHandler(delegate, (source, err) => calls.push([source, err]));
      const error = new Error('write failed');

      const result = await wrapped.error(error, undefined, 1);

      assert.deepStrictEqual(result, { action: 1 });
      assert.strictEqual(delegate.errors, 1);
      assert.deepStrictEqual(calls, [[SOURCE_LANGUAGE_CLIENT_ERROR_HANDLER, error]]);
    });

    test('does not report a closed connection (server lifecycle, #845)', async () => {
      const calls: unknown[] = [];
      const delegate = inner();
      const wrapped = wrapErrorHandler(delegate, (...args) => calls.push(args));

      await wrapped.closed();

      assert.strictEqual(delegate.closedCalls, 1);
      assert.strictEqual(calls.length, 0);
    });

    test('the lazy handler builds the default handler once, on first use', async () => {
      let created = 0;
      const delegate = inner();
      const lazy = createReportingErrorHandler(() => {
        created++;
        return delegate;
      });
      assert.strictEqual(created, 0);

      await lazy.error(new Error('a'), undefined, 1);
      await lazy.closed();

      assert.strictEqual(created, 1);
      assert.strictEqual(delegate.errors, 1);
      assert.strictEqual(delegate.closedCalls, 1);
    });
  });

  suite('transmission through the shared reporter', () => {
    const originalEnv = process.env.REQNROLL_TELEMETRY_ENABLED;

    setup(() => resetTelemetryReporterForTests());

    teardown(() => {
      if (originalEnv === undefined) delete process.env.REQNROLL_TELEMETRY_ENABLED;
      else process.env.REQNROLL_TELEMETRY_ENABLED = originalEnv;
    });

    function withStubbedReporter(
      fn: (calls: Sent[], context: vscode.ExtensionContext) => void,
    ): void {
      const calls: Sent[] = [];
      const proto = TelemetryReporter.prototype as unknown as {
        sendTelemetryEvent: (name: string, props?: Record<string, string>) => void;
      };
      const original = proto.sendTelemetryEvent;
      proto.sendTelemetryEvent = (name, props) => {
        calls.push({ name, props: props ?? {} });
      };
      const context = { subscriptions: [] } as unknown as vscode.ExtensionContext;
      try {
        fn(calls, context);
      } finally {
        proto.sendTelemetryEvent = original;
        for (const sub of context.subscriptions) sub.dispose();
      }
    }

    test('an activation exception is sent as UnhandledException stamped with the client identity', () => {
      delete process.env.REQNROLL_TELEMETRY_ENABLED;

      withStubbedReporter((calls, context) => {
        ensureTelemetryReporter(context);
        reportClientException('Activation', new RangeError('transmission-test-1'));

        assert.strictEqual(calls.length, 1);
        assert.strictEqual(calls[0].name, 'UnhandledException');
        assert.strictEqual(calls[0].props.ExceptionType, 'RangeError');
        assert.strictEqual(calls[0].props.Source, 'Activation');
        assert.strictEqual(calls[0].props.ExceptionOrigin, 'Client');
        assert.strictEqual(calls[0].props.IdeClient, 'vscode');
      });
    });

    test('nothing is sent when REQNROLL_TELEMETRY_ENABLED is "0"', () => {
      process.env.REQNROLL_TELEMETRY_ENABLED = '0';

      withStubbedReporter((calls, context) => {
        ensureTelemetryReporter(context);
        reportClientException('Activation', new RangeError('transmission-test-2'));

        assert.strictEqual(calls.length, 0);
      });
    });
  });
});
